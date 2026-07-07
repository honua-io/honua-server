// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Result of validating a presented <c>Mcp-Session-Id</c> against the registry
/// and the calling principal.
/// </summary>
internal enum McpSessionValidation
{
    /// <summary>The session exists, is unexpired, and is bound to the caller.</summary>
    Valid,

    /// <summary>The session id is unknown, was terminated, or has expired (idle TTL).</summary>
    Unknown,

    /// <summary>
    /// The session exists but is bound to a different principal than the caller
    /// (a different authenticated identity, or anonymous on an authenticated
    /// session and vice versa).
    /// </summary>
    PrincipalMismatch
}

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
/// the session sticky"). The 2026 MCP RC is moving toward a stateless core, so
/// this type deliberately keeps its surface small and externalizable: a
/// Redis-backed <c>IMcpSessionStore</c> could replace the in-memory dictionaries
/// (session record, idle-TTL sweep, LRU cap, principal binding, and the
/// job→session index) behind the same methods when multi-node MCP fan-out lands
/// (honua-server#1954 follow-up). Nothing here holds a reference the store could
/// not serialize except the per-session notification <see cref="Channel"/> and
/// its lifetime token, which are inherently node-local (they back the SSE stream
/// held open on this node) and would stay node-local under a Redis design.
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
/// <b>Session lifecycle (A3 hardening; honua-server#2537).</b> A session is bound
/// at <c>initialize</c> to the authenticated principal (or to anonymous where the
/// endpoint allows it); a later request bearing the id but a different principal
/// is rejected. Sessions expire on a sliding idle TTL and the registry enforces a
/// maximum-session cap so anonymous <c>initialize</c> can no longer grow host
/// memory without limit. Expired/evicted ids validate as
/// <see cref="McpSessionValidation.Unknown"/>, i.e. HTTP 404, so clients
/// re-initialize cleanly.
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

    /// <summary>
    /// Principal key stored for a session established without an authenticated
    /// principal (anonymous <c>initialize</c>, only possible where the endpoint
    /// itself allows anonymous access). Distinct from any authenticated key so an
    /// anonymous caller can never ride an authenticated session and vice versa.
    /// </summary>
    public const string AnonymousPrincipalKey = "";

    private const int SessionIdByteLength = 32; // 256 bits.

    /// <summary>
    /// Maximum number of buffered notification frames per session. Bounded so a
    /// disconnected or slow client cannot grow host memory without limit; the
    /// oldest frame is dropped when the buffer is full.
    /// </summary>
    private const int NotificationBufferCapacity = 256;

    private readonly ConcurrentDictionary<string, McpSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _jobSessions = new(StringComparer.Ordinal);

    // Serializes capacity enforcement (prune + evict + admit) so the cap is never
    // transiently exceeded under a burst of concurrent initializes. Reads
    // (validation, enqueue, reader lookup) stay lock-free on the concurrent map.
    private readonly object _capacityGate = new();

    private readonly int _maxSessions;
    private readonly TimeSpan _idleTimeout;
    private readonly McpSessionEvictionPolicy _evictionPolicy;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a session manager with the supplied lifecycle bounds. The
    /// parameterless defaults match <see cref="McpOptions"/> so unit tests and
    /// isolated compositions get a sane, memory-bounded registry without wiring
    /// configuration.
    /// </summary>
    public McpSessionManager(
        int maxSessions = 10_000,
        TimeSpan? idleTimeout = null,
        McpSessionEvictionPolicy evictionPolicy = McpSessionEvictionPolicy.EvictLeastRecentlyUsed,
        TimeProvider? timeProvider = null)
    {
        _maxSessions = maxSessions > 0 ? maxSessions : 1;
        var idle = idleTimeout ?? TimeSpan.FromMinutes(30);
        _idleTimeout = idle > TimeSpan.Zero ? idle : TimeSpan.FromMinutes(30);
        _evictionPolicy = evictionPolicy;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Creates and registers a new session bound to the anonymous principal,
    /// returning its id. Retained for callers and tests that do not thread a
    /// principal; production <c>initialize</c> handling uses
    /// <see cref="TryCreateSession"/> so it can honor the capacity policy.
    /// </summary>
    public string CreateSession()
    {
        TryCreateSession(AnonymousPrincipalKey, out var id);
        return id;
    }

    /// <summary>
    /// Creates and registers a new session bound to <paramref name="principalKey"/>
    /// (use <see cref="AnonymousPrincipalKey"/> for an anonymous caller). Enforces
    /// the idle TTL and the maximum-session cap: expired sessions are swept first,
    /// then — if still at capacity — the configured
    /// <see cref="McpSessionEvictionPolicy"/> is applied. Returns <c>false</c> with
    /// an empty <paramref name="sessionId"/> only when the cap is reached and the
    /// policy is <see cref="McpSessionEvictionPolicy.RejectNew"/>. Invoked when the
    /// server accepts an <c>initialize</c> request in stateful mode.
    /// </summary>
    public bool TryCreateSession(string? principalKey, out string sessionId)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_capacityGate)
        {
            SweepExpired(now);

            if (_sessions.Count >= _maxSessions)
            {
                if (_evictionPolicy == McpSessionEvictionPolicy.RejectNew)
                {
                    sessionId = string.Empty;
                    return false;
                }

                EvictLeastRecentlyUsed(_sessions.Count - _maxSessions + 1);
            }

            var id = GenerateSessionId();
            _sessions[id] = new McpSession(principalKey ?? AnonymousPrincipalKey, now);
            sessionId = id;
            return true;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the supplied session id is currently active and
    /// unexpired. Expired sessions are treated as invalid (and swept lazily).
    /// </summary>
    public bool IsValid(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var session))
        {
            return false;
        }

        if (IsExpired(session, _timeProvider.GetUtcNow()))
        {
            Terminate(sessionId);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates a presented session id against the calling principal for a
    /// subsequent (non-<c>initialize</c>) request. On <see cref="McpSessionValidation.Valid"/>
    /// the session's sliding idle window is refreshed. An unknown, terminated, or
    /// idle-expired id yields <see cref="McpSessionValidation.Unknown"/> (HTTP 404,
    /// re-initialize); a live session bound to a different principal yields
    /// <see cref="McpSessionValidation.PrincipalMismatch"/>.
    /// </summary>
    public McpSessionValidation ValidateAccess(string sessionId, string? principalKey)
    {
        if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var session))
        {
            return McpSessionValidation.Unknown;
        }

        var now = _timeProvider.GetUtcNow();
        if (IsExpired(session, now))
        {
            Terminate(sessionId);
            return McpSessionValidation.Unknown;
        }

        if (!string.Equals(session.PrincipalKey, principalKey ?? AnonymousPrincipalKey, StringComparison.Ordinal))
        {
            return McpSessionValidation.PrincipalMismatch;
        }

        // Sliding TTL: any accepted request keeps the session alive.
        session.Touch(now);
        return McpSessionValidation.Valid;
    }

    /// <summary>
    /// Terminates a session so subsequent requests bearing its id are rejected
    /// with HTTP 404. Completes the session's notification channel (so any open
    /// SSE reader unblocks and the stream closes) and cancels its lifetime token
    /// (so any in-flight progress bridge stops). Returns <c>true</c> when an
    /// active session was removed. Invoked when a client issues <c>DELETE /mcp</c>
    /// and by the idle-TTL / capacity sweep.
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
    /// drain queued server-to-client frames, refreshing the sliding idle window so
    /// an open GET stream keeps the session alive. Returns <c>false</c> when the
    /// session is unknown, terminated, or idle-expired.
    /// </summary>
    public bool TryGetReader(string sessionId, out ChannelReader<string> reader)
    {
        if (!string.IsNullOrEmpty(sessionId) && _sessions.TryGetValue(sessionId, out var session))
        {
            var now = _timeProvider.GetUtcNow();
            if (IsExpired(session, now))
            {
                Terminate(sessionId);
                reader = null!;
                return false;
            }

            session.Touch(now);
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
    /// blocks the caller. Server-initiated enqueues do not refresh the idle window;
    /// the TTL tracks client activity.
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

    private bool IsExpired(McpSession session, DateTimeOffset now) =>
        now - session.LastAccessUtc > _idleTimeout;

    /// <summary>
    /// Removes every idle-expired session. Callers hold <see cref="_capacityGate"/>;
    /// <see cref="Terminate"/> is itself safe against concurrent readers.
    /// </summary>
    private void SweepExpired(DateTimeOffset now)
    {
        foreach (var pair in _sessions)
        {
            if (IsExpired(pair.Value, now))
            {
                Terminate(pair.Key);
            }
        }
    }

    /// <summary>
    /// Evicts the <paramref name="count"/> least-recently-used sessions. Called
    /// under <see cref="_capacityGate"/> after an expiry sweep left the table at
    /// capacity, so an <c>initialize</c> can still be admitted.
    /// </summary>
    private void EvictLeastRecentlyUsed(int count)
    {
        if (count <= 0)
        {
            return;
        }

        var victims = _sessions
            .OrderBy(pair => pair.Value.LastAccessUtc)
            .Take(count)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var id in victims)
        {
            Terminate(id);
        }
    }

    private static string GenerateSessionId()
    {
        Span<byte> buffer = stackalloc byte[SessionIdByteLength];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexStringLower(buffer);
    }

    private sealed class McpSession
    {
        public McpSession(string principalKey, DateTimeOffset createdUtc)
        {
            PrincipalKey = principalKey;
            LastAccessUtc = createdUtc;
        }

        /// <summary>
        /// Stable key of the principal the session was bound to at <c>initialize</c>
        /// (<see cref="AnonymousPrincipalKey"/> for an anonymous session).
        /// </summary>
        public string PrincipalKey { get; }

        /// <summary>
        /// Last time a client request touched this session; drives the sliding idle
        /// TTL. Written only under the concurrent map's per-entry guarantees via
        /// <see cref="Touch"/>; races only shorten or lengthen the window by one
        /// request, which is harmless.
        /// </summary>
        public DateTimeOffset LastAccessUtc { get; private set; }

        public void Touch(DateTimeOffset now) => LastAccessUtc = now;

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
