// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Tracks MCP Streamable-HTTP sessions for the operator surface. The
/// Streamable-HTTP transport (MCP 2025-03-26) lets a server assign a session id
/// during <c>initialize</c> and return it on the <c>Mcp-Session-Id</c> response
/// header; the client MUST then echo that id on every subsequent request. The
/// server validates the id and returns HTTP 404 once a session is unknown or
/// terminated so the client knows to re-initialize.
/// See https://modelcontextprotocol.io/specification/2025-03-26/basic/transports#session-management.
/// </summary>
/// <remarks>
/// <para>
/// This is an in-process registry: session state lives in the host's memory and
/// is not shared across replicas. That matches the current single-process MCP
/// usage and the session-affinity expectation in the spec ("the server MAY make
/// the session sticky"). A Redis-backed implementation can replace this behind
/// the same surface when multi-node MCP fan-out lands (honua-server#1954
/// follow-up).
/// </para>
/// <para>
/// Each session owns a bounded notification channel (honua-server#1954). The
/// <c>GET /mcp</c> SSE handler drains the channel and writes each queued payload
/// as one Server-Sent-Events <c>message</c> frame; the notification publisher and
/// job-progress bridge enqueue onto it. The channel drops the oldest frame under
/// backpressure because progress is intentionally lossy — a slow or absent reader
/// must never block the producing job. The session also records the job ids it
/// owns so progress for a job routes back to the session that started it.
/// </para>
/// <para>
/// Session ids are required by the spec to be globally unique, cryptographically
/// secure, and to contain only visible ASCII characters (0x21–0x7E). A 256-bit
/// random value rendered as lowercase hex satisfies all three.
/// </para>
/// </remarks>
internal sealed class McpSessionManager
{
    /// <summary>
    /// HTTP header carrying the MCP session id on initialize responses and on
    /// every subsequent client request, per the Streamable-HTTP transport.
    /// </summary>
    public const string SessionHeaderName = "Mcp-Session-Id";

    private const int SessionIdByteLength = 32; // 256 bits.

    /// <summary>
    /// Maximum number of buffered notification frames per session. Bounded so a
    /// disconnected or slow client cannot grow host memory without limit; the
    /// oldest frame is dropped when the buffer is full.
    /// </summary>
    private const int NotificationBufferCapacity = 256;

    private readonly ConcurrentDictionary<string, McpSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _jobSessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates and registers a new session, returning its id. Invoked when the
    /// server accepts an <c>initialize</c> request and chooses to operate in
    /// stateful (session-bearing) mode.
    /// </summary>
    public string CreateSession()
    {
        var id = GenerateSessionId();
        _sessions[id] = new McpSession();
        return id;
    }

    /// <summary>
    /// Returns <c>true</c> when the supplied session id is currently active.
    /// </summary>
    public bool IsValid(string sessionId) =>
        !string.IsNullOrEmpty(sessionId) && _sessions.ContainsKey(sessionId);

    /// <summary>
    /// Terminates a session so subsequent requests bearing its id are rejected
    /// with HTTP 404. Completes the session's notification channel (so any open
    /// SSE reader unblocks and the stream closes) and cancels its lifetime token
    /// (so any in-flight progress bridge stops). Returns <c>true</c> when an
    /// active session was removed. Invoked when a client issues <c>DELETE /mcp</c>.
    /// </summary>
    public bool Terminate(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !_sessions.TryRemove(sessionId, out var session))
        {
            return false;
        }

        session.Channel.Writer.TryComplete();
        session.Lifetime.Cancel();
        session.Lifetime.Dispose();
        return true;
    }

    /// <summary>
    /// Obtains the notification-stream reader for a session so the SSE handler can
    /// drain queued server-to-client frames. Returns <c>false</c> when the session
    /// is unknown or terminated.
    /// </summary>
    public bool TryGetReader(string sessionId, out ChannelReader<string> reader)
    {
        if (!string.IsNullOrEmpty(sessionId) && _sessions.TryGetValue(sessionId, out var session))
        {
            reader = session.Channel.Reader;
            return true;
        }

        reader = null!;
        return false;
    }

    /// <summary>
    /// A cancellation token tied to the session lifetime. Cancels when the session
    /// is terminated. Returns <see cref="CancellationToken.None"/> for an unknown
    /// session (already-terminated work simply ends).
    /// </summary>
    public CancellationToken GetLifetimeToken(string sessionId) =>
        !string.IsNullOrEmpty(sessionId) && _sessions.TryGetValue(sessionId, out var session)
            ? session.Lifetime.Token
            : CancellationToken.None;

    /// <summary>
    /// Enqueues a pre-serialized notification frame for delivery over the session's
    /// SSE stream. Returns <c>false</c> when the session is unknown/terminated.
    /// Under buffer pressure the oldest queued frame is dropped, so this never
    /// blocks the caller.
    /// </summary>
    public bool TryEnqueue(string sessionId, string payload)
    {
        if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var session))
        {
            return false;
        }

        return session.Channel.Writer.TryWrite(payload);
    }

    /// <summary>
    /// Records that <paramref name="jobId"/> was started within
    /// <paramref name="sessionId"/> so the progress bridge can route the job's
    /// progress notifications back to the owning session's SSE stream.
    /// </summary>
    public void AssociateJob(string sessionId, string jobId)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(jobId))
        {
            return;
        }

        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.Jobs[jobId] = 0;
            _jobSessions[jobId] = sessionId;
        }
    }

    /// <summary>
    /// Resolves the session that owns a job, if any, so a job-progress source can
    /// find the SSE stream to notify.
    /// </summary>
    public bool TryGetJobSession(string jobId, out string sessionId)
    {
        if (!string.IsNullOrEmpty(jobId) && _jobSessions.TryGetValue(jobId, out var resolved))
        {
            sessionId = resolved;
            return true;
        }

        sessionId = string.Empty;
        return false;
    }

    /// <summary>
    /// Snapshot of the currently active session ids, used to broadcast
    /// catalog-change notifications (<c>tools/list_changed</c> /
    /// <c>resources/list_changed</c>) to every connected client.
    /// </summary>
    public IReadOnlyCollection<string> ActiveSessionIds => _sessions.Keys.ToArray();

    private static string GenerateSessionId()
    {
        Span<byte> buffer = stackalloc byte[SessionIdByteLength];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexStringLower(buffer);
    }

    private sealed class McpSession
    {
        public Channel<string> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<string>(
            new BoundedChannelOptions(NotificationBufferCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        public ConcurrentDictionary<string, byte> Jobs { get; } = new(StringComparer.Ordinal);

        public CancellationTokenSource Lifetime { get; } = new();
    }
}
