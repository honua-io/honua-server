// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;

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

    private readonly ConcurrentDictionary<string, byte> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates and registers a new session, returning its id. Invoked when the
    /// server accepts an <c>initialize</c> request and chooses to operate in
    /// stateful (session-bearing) mode.
    /// </summary>
    public string CreateSession()
    {
        var id = GenerateSessionId();
        _sessions[id] = 0;
        return id;
    }

    /// <summary>
    /// Returns <c>true</c> when the supplied session id is currently active.
    /// </summary>
    public bool IsValid(string sessionId) =>
        !string.IsNullOrEmpty(sessionId) && _sessions.ContainsKey(sessionId);

    /// <summary>
    /// Terminates a session so subsequent requests bearing its id are rejected
    /// with HTTP 404. Returns <c>true</c> when an active session was removed.
    /// Invoked when a client issues <c>DELETE /mcp</c> to end the session.
    /// </summary>
    public bool Terminate(string sessionId) =>
        !string.IsNullOrEmpty(sessionId) && _sessions.TryRemove(sessionId, out _);

    private static string GenerateSessionId()
    {
        Span<byte> buffer = stackalloc byte[SessionIdByteLength];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexStringLower(buffer);
    }
}
