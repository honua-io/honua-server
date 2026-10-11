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
    /// Development convenience. When <see langword="true"/> a <c>POST /mcp</c>
    /// request that presents a well-formed <c>Mcp-Session-Id</c> no instance holds
    /// is served as if it were session-less instead of being rejected with
    /// HTTP 404 (honua-server#3027). That silently drops whatever the client
    /// negotiated at <c>initialize</c> (the workflow view, so <c>tools/list</c>
    /// changes mid-session, and the elicitation capability), which is why it is
    /// <em>off</em> by default everywhere except the Development environment: a
    /// session negotiated on any instance is honoured by every instance (through
    /// the shared Redis session store when Redis is connected) or refused with
    /// HTTP 404 so a conforming client re-initializes, never silently downgraded.
    /// An explicit <c>Mcp:StatelessSessionFallback</c> setting overrides the
    /// environment default. Malformed ids (non visible-ASCII or overlong) always
    /// 404 regardless of this setting.
    /// </summary>
    public bool StatelessSessionFallback { get; set; }

    /// <summary>
    /// Environment default for <see cref="StatelessSessionFallback"/>: on only in
    /// Development. Applied before configuration binding, so an explicit setting wins.
    /// </summary>
    internal static bool DefaultStatelessSessionFallback(IHostEnvironment? environment) =>
        environment?.IsDevelopment() == true;

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

    /// <summary>
    /// Delay advised when reject-new session capacity backpressure is returned. Default 5 seconds.
    /// </summary>
    public int SessionCapacityRetryAfterSeconds { get; set; } = 5;

    /// <summary>
    /// Maximum number of concurrently tracked anonymous sessions (SEC-18). Anonymous
    /// sessions form their own pool inside <see cref="MaxSessions"/>: admitting one
    /// only ever evicts another anonymous session, never an authenticated one, and an
    /// authenticated <c>initialize</c> at capacity evicts anonymous sessions first.
    /// Clamped to <see cref="MaxSessions"/>. Default 1,000.
    /// </summary>
    public int MaxAnonymousSessions { get; set; } = 1_000;

    /// <summary>
    /// Maximum number of elements in one JSON-RPC batch on <c>POST /mcp</c> (SEC-18).
    /// A longer batch is answered with a single <c>invalid_request</c> error and none
    /// of its elements is dispatched. Default 50.
    /// </summary>
    public int MaxBatchSize { get; set; } = 50;

    /// <summary>
    /// Maximum <c>POST /mcp</c> request body size in bytes (SEC-18). A larger body is
    /// refused with HTTP 413 before it is parsed. The default, 10 MiB, leaves room for
    /// the 4 MiB inline dataset accepted by <c>honua_ingest_dataset</c> after JSON
    /// string escaping.
    /// </summary>
    public long MaxRequestBodyBytes { get; set; } = 10L * 1024 * 1024;
}
