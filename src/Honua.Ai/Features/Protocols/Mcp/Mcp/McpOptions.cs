// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Policy applied when a new MCP session would exceed
/// <see cref="McpOptions.MaxSessions"/>.
/// </summary>
internal enum McpSessionEvictionPolicy
{
    /// <summary>
    /// Evict the least-recently-used session to make room for the new one. This
    /// keeps a public <c>initialize</c> endpoint responsive under load while the
    /// idle TTL and the cap together bound host memory. An abandoned or idle
    /// session is the most likely eviction target because active sessions keep
    /// refreshing their last-access timestamp.
    /// </summary>
    EvictLeastRecentlyUsed,

    /// <summary>
    /// Reject the new <c>initialize</c> with a retryable
    /// <c>unavailable</c> error and leave existing sessions untouched. Prevents a
    /// burst of anonymous initializes from evicting live sessions, at the cost of
    /// briefly refusing new sessions when the table is full.
    /// </summary>
    RejectNew
}

/// <summary>
/// Host-tunable options for the MCP Streamable-HTTP surface: whether the optional
/// server-initiated <c>GET /mcp</c> SSE stream is offered, and the session
/// lifecycle bounds (idle TTL, capacity, and the eviction policy) that keep an
/// anonymous-capable <c>initialize</c> endpoint from growing host memory without
/// limit (A3 session hardening; honua-server#2537).
/// </summary>
/// <remarks>
/// Bound from the <c>Mcp</c> configuration section. Defaults are chosen for the
/// baseline deployment reality (serverless / non-streaming ingress such as
/// CloudFront → API Gateway HTTP API → Lambda): server-initiated streaming is
/// <em>off</em> so <c>GET /mcp</c> answers <c>405 Method Not Allowed</c> per the
/// Streamable-HTTP spec and spec-compliant SDK clients skip the optional stream
/// instead of hanging it at a non-streaming origin.
/// </remarks>
internal sealed class McpOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Mcp";

    /// <summary>
    /// When <see langword="true"/> the server offers the optional server-initiated
    /// <c>GET /mcp</c> SSE stream (progress / <c>*/list_changed</c> notifications);
    /// when <see langword="false"/> (the default) <c>GET /mcp</c> returns
    /// <c>405 Method Not Allowed</c> with <c>Allow: POST, DELETE</c>. Enable this
    /// only behind ingress that can hold a streaming response open (e.g. nginx with
    /// buffering disabled, an ALB, or a direct connection) — not behind a buffering
    /// serverless gateway, where the SDK's standalone GET stream would hang and
    /// churn (honua-server#2537).
    /// </summary>
    public bool ServerInitiatedStreamEnabled { get; set; }

    /// <summary>
    /// When <see langword="true"/> (the default) a <c>POST /mcp</c> request that
    /// presents a well-formed but unknown <c>Mcp-Session-Id</c> is served as if it
    /// were session-less instead of being rejected with HTTP 404. Session state is
    /// per instance, so on a multi-instance deployment without sticky routing
    /// (e.g. the multi-container Lambda demo) a spec-compliant client that
    /// initialized against one instance would otherwise 404 on every POST routed
    /// to a different instance (honua-server#3027). Serving the request
    /// statelessly is safe because every POST is independently authenticated and
    /// authorized; the only session-negotiated state (elicitation capability,
    /// SSE progress routing) degrades to the documented session-less fallbacks.
    /// When <see langword="false"/> the strict Streamable-HTTP (MCP 2025-03-26)
    /// behavior is kept: an unknown id answers HTTP 404 so the client
    /// re-initializes. Malformed ids (non visible-ASCII or overlong) always 404
    /// regardless of this setting.
    /// </summary>
    public bool StatelessSessionFallback { get; set; } = true;

    /// <summary>
    /// Maximum number of concurrently tracked sessions. When reached, a new
    /// <c>initialize</c> is handled per <see cref="SessionEvictionPolicy"/>. Bounds
    /// host memory on a public endpoint. Default 10,000.
    /// </summary>
    public int MaxSessions { get; set; } = 10_000;

    /// <summary>
    /// Sliding idle timeout after which an untouched session expires and is swept.
    /// Every accepted request (or an opened GET stream) on a session refreshes the
    /// window. Default 30 minutes. Expired sessions surface the same HTTP 404 the
    /// transport already uses for unknown sessions, so clients re-initialize
    /// cleanly. Bind as a <c>TimeSpan</c> string (e.g. <c>00:30:00</c>).
    /// </summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Policy applied when <see cref="MaxSessions"/> is reached. Defaults to
    /// <see cref="McpSessionEvictionPolicy.EvictLeastRecentlyUsed"/>.
    /// </summary>
    public McpSessionEvictionPolicy SessionEvictionPolicy { get; set; }
        = McpSessionEvictionPolicy.EvictLeastRecentlyUsed;
}
