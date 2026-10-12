// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using StackExchange.Redis;

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
/// Outcome of <see cref="McpSessionManager.TerminateAsync"/>.
/// </summary>
internal enum McpSessionTermination
{
    /// <summary>The session was terminated on every instance.</summary>
    Terminated,

    /// <summary>The session was not held by this instance.</summary>
    NotFound,

    /// <summary>
    /// The session is stateless (<see cref="McpSessionMode.Stateless"/>): its
    /// node-local work here was ended, but the id stays valid on every instance
    /// until its lifetime ends, so the server does not claim to have terminated it.
    /// </summary>
    NotRevocable,

    /// <summary>The shared record could not be removed; the session may still be live elsewhere.</summary>
    StoreUnavailable
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
/// Session records are kept in this host's memory and, when a shared
/// <see cref="IMcpSessionStore"/> is composed (Redis connected; the governed
/// control plane requires it), also in that store. With a store the in-memory
/// table is a read-through cache: a request bearing an id this instance does
/// not hold is resolved from the store, so a session negotiated on any
/// instance, including its workflow view, is honoured by every instance or
/// refused with HTTP 404, never silently downgraded. The store is
/// authoritative for existence: every accepted request slides the record's
/// expiry, and a record that is gone (deleted or expired elsewhere) evicts the
/// local copy. Without a store this is a single-instance registry, as before.
/// The per-session notification <see cref="Channel"/>, its lifetime token, and
/// the job-to-session index are inherently node-local (they back the SSE
/// stream held open on this node) and are never shared.
/// </para>
/// <para>
/// Without a store on a host that runs as several instances, sessions are
/// stateless (<see cref="McpSessionMode.Stateless"/>): the id minted at
/// <c>initialize</c> is a signed token (<see cref="McpStatelessSessionTokens"/>)
/// carrying the negotiated view, elicitation flag, and principal binding, so any
/// instance validates it and rebuilds the session with no shared memory. The
/// in-memory table is then only a cache of node-local state; an id that fails
/// the signature, lifetime, or principal check is refused exactly as before.
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
    // Also serializes removal tombstones with Adopt: a broadcast can arrive before
    // the cache-miss read installs the record, and Terminate then finds nothing.
    private readonly object _capacityGate = new();
    private readonly Dictionary<string, DateTimeOffset> _removedSessions = new(StringComparer.Ordinal);

    private readonly int _maxSessions;
    private readonly int _maxAnonymousSessions;
    private readonly TimeSpan _idleTimeout;
    private readonly McpSessionEvictionPolicy _evictionPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly IMcpSessionStore? _store;
    private readonly McpStatelessSessionTokens? _statelessTokens;
    private readonly ILogger? _logger;

    // Held for the manager's (process) lifetime; the multiplexer owns teardown.
    private readonly IDisposable? _removalSubscription;

    /// <summary>
    /// Creates a session manager with the supplied lifecycle bounds. The
    /// parameterless defaults match <see cref="McpOptions"/> so unit tests and
    /// isolated compositions get a sane, memory-bounded registry without wiring
    /// configuration. <paramref name="maxAnonymousSessions"/> bounds the anonymous
    /// pool inside <paramref name="maxSessions"/> (SEC-18) and is clamped to it.
    /// </summary>
    public McpSessionManager(
        int maxSessions = 10_000,
        TimeSpan? idleTimeout = null,
        McpSessionEvictionPolicy evictionPolicy = McpSessionEvictionPolicy.EvictLeastRecentlyUsed,
        TimeProvider? timeProvider = null,
        int maxAnonymousSessions = 1_000,
        IMcpSessionStore? store = null,
        ILogger? logger = null,
        McpStatelessSessionTokens? statelessTokens = null)
    {
        _maxSessions = maxSessions > 0 ? maxSessions : 1;
        _maxAnonymousSessions = Math.Clamp(maxAnonymousSessions, 1, _maxSessions);
        var idle = idleTimeout ?? TimeSpan.FromMinutes(30);
        _idleTimeout = idle > TimeSpan.Zero ? idle : TimeSpan.FromMinutes(30);
        _evictionPolicy = evictionPolicy;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _store = store;

        // A shared store is authoritative and supports revocation; stateless ids are
        // only for hosts without one.
        _statelessTokens = store is null ? statelessTokens : null;
        _logger = logger;

        // A DELETE served by another replica must end this replica's node-local
        // session work (SSE channel, progress bridges) immediately, not on the next
        // request that happens to land here.
        if (_store is not null)
        {
            try
            {
                _removalSubscription = _store.SubscribeToRemovals(id =>
                {
                    // Record the removal even when this replica has not adopted the
                    // session yet. A read already in flight can otherwise install it
                    // after Terminate finds no local entry.
                    RememberRemoval(id);
                    Terminate(id);
                });
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                LogStoreFailure(ex, "subscribe");
            }
        }
    }

    /// <summary>
    /// <c>true</c> when sessions are shared across instances through an
    /// <see cref="IMcpSessionStore"/>.
    /// </summary>
    public bool HasSharedStore => _store is not null;

    /// <summary>How this instance keeps sessions.</summary>
    public McpSessionMode Mode =>
        _store is not null ? McpSessionMode.Shared
        : _statelessTokens is not null ? McpSessionMode.Stateless
        : McpSessionMode.InMemory;

    /// <summary>
    /// <c>true</c> when this instance receives other instances' session removals.
    /// </summary>
    public bool ReceivesSharedRemovals => _removalSubscription is not null;

    /// <summary>
    /// Creates and registers a new session bound to the anonymous principal,
    /// returning its id. Retained for callers and tests that do not thread a
    /// principal; production <c>initialize</c> handling uses
    /// <see cref="TryCreateSession(string?, bool, out string)"/> so it can honor
    /// the capacity policy.
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
    /// <see cref="McpSessionEvictionPolicy"/> is applied. Anonymous sessions form a
    /// separate pool bounded by the anonymous cap (SEC-18): an anonymous session
    /// only ever evicts another anonymous session, and an authenticated session at
    /// capacity evicts anonymous sessions before authenticated ones. Returns
    /// <c>false</c> with an empty <paramref name="sessionId"/> when the cap is
    /// reached and the policy is <see cref="McpSessionEvictionPolicy.RejectNew"/>,
    /// or when an anonymous session would have to evict an authenticated one.
    /// Invoked when the server accepts an <c>initialize</c> request in stateful mode.
    /// </summary>
    public bool TryCreateSession(string? principalKey, out string sessionId) =>
        TryCreateSession(principalKey, elicitationSupported: false, out sessionId);

    /// <summary>
    /// Creates and registers a new session, additionally recording whether the
    /// client advertised the MCP elicitation capability at <c>initialize</c>
    /// (honua-server#2484). Behaves identically to
    /// <see cref="TryCreateSession(string?, out string)"/> otherwise; the flag is
    /// later read by <see cref="SupportsElicitation"/> so clarification-emitting
    /// tools can choose the elicitation projection over the proprietary envelope.
    /// </summary>
    public bool TryCreateSession(string? principalKey, bool elicitationSupported, out string sessionId) =>
        TryCreateSession(principalKey, elicitationSupported, workflowView: null, out sessionId);

    /// <summary>
    /// Creates and registers a new session, additionally recording the workflow
    /// discovery view the client negotiated at <c>initialize</c>
    /// (honua-server#3428) — the session leg of the view negotiation contract.
    /// The bound name narrows <c>tools/list</c> discovery for the life of the
    /// session; it confers no authority, and every <c>tools/call</c> is still
    /// independently reauthenticated and reauthorized.
    /// </summary>
    public bool TryCreateSession(
        string? principalKey,
        bool elicitationSupported,
        string? workflowView,
        out string sessionId) =>
        TryCreateSessionCore(principalKey, elicitationSupported, workflowView, out sessionId);

    /// <summary>
    /// Creates and registers a new session exactly as
    /// <see cref="TryCreateSession(string?, bool, string?, out string)"/> does and,
    /// when a shared <see cref="IMcpSessionStore"/> is composed, persists its record
    /// before returning so any instance can serve the very next request. With a
    /// store, capacity eviction only drops entries from this instance's cache: the
    /// local least-recently-used order reflects only this replica's traffic, so it
    /// must not end a session that may be active on another replica. A store
    /// failure is logged and leaves a session only this instance holds; other
    /// instances then answer it with HTTP 404 and the client re-initializes.
    /// Returns <c>null</c> when the capacity policy refused the session.
    /// </summary>
    public async Task<string?> TryCreateSessionAsync(
        string? principalKey,
        bool elicitationSupported,
        string? workflowView,
        CancellationToken cancellationToken)
    {
        if (!TryCreateSessionCore(principalKey, elicitationSupported, workflowView, out var sessionId))
        {
            return null;
        }

        if (_store is not null)
        {
            var record = new McpSessionRecord(
                sessionId,
                principalKey ?? AnonymousPrincipalKey,
                elicitationSupported,
                workflowView,
                _timeProvider.GetUtcNow());
            try
            {
                await _store.SaveAsync(record, _idleTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                LogStoreFailure(ex, "save");
            }
        }

        return sessionId;
    }

    private bool TryCreateSessionCore(
        string? principalKey,
        bool elicitationSupported,
        string? workflowView,
        out string sessionId)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_capacityGate)
        {
            SweepExpired(now);

            var key = principalKey ?? AnonymousPrincipalKey;
            var anonymous = IsAnonymous(key);
            var excess = _sessions.Count - _maxSessions + 1;
            if (anonymous)
            {
                excess = Math.Max(excess, _sessions.Values.Count(static s => s.IsAnonymous) - _maxAnonymousSessions + 1);
            }

            if (excess > 0)
            {
                if (_evictionPolicy == McpSessionEvictionPolicy.RejectNew)
                {
                    sessionId = string.Empty;
                    return false;
                }

                EvictLeastRecentlyUsed(excess, anonymousOnly: anonymous);

                // Only reachable for an anonymous session when the table holds too few
                // anonymous sessions to make room: it never displaces an authenticated one.
                if (_sessions.Count >= _maxSessions)
                {
                    sessionId = string.Empty;
                    return false;
                }
            }

            var id = _statelessTokens?.Mint(key, elicitationSupported, workflowView) ?? GenerateSessionId();
            _sessions[id] = new McpSession(
                key,
                now,
                elicitationSupported,
                workflowView);
            sessionId = id;
            return true;
        }
    }

    /// <summary>
    /// Validates a presented session id like <see cref="ValidateAccess"/>, consulting
    /// the shared <see cref="IMcpSessionStore"/> when one is composed. With a store,
    /// the store is authoritative for existence and for the idle window:
    /// <list type="bullet">
    /// <item>A session this instance does not hold is read through from the store and
    /// cached, so its negotiated state (workflow view, elicitation capability,
    /// principal binding) is honoured here exactly as on the instance that minted it.</item>
    /// <item>A cached session is confirmed against the store before anything else, so
    /// a session kept alive on another replica is never torn down here because this
    /// replica's copy looks idle, and one terminated or expired elsewhere is dropped
    /// (<see cref="McpSessionValidation.Unknown"/>), including for a caller whose
    /// principal does not match.</item>
    /// <item>A principal mismatch never slides the shared expiry.</item>
    /// </list>
    /// If the store is unreachable a cached, locally unexpired session keeps being
    /// served and an unknown one validates as unknown (HTTP 404), never as a
    /// session-less request.
    /// </summary>
    public async Task<McpSessionValidation> ValidateAccessAsync(
        string sessionId,
        string? principalKey,
        CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            return ValidateAccess(sessionId, principalKey);
        }

        if (string.IsNullOrEmpty(sessionId) || !McpEndpointExtensions.IsWellFormedSessionId(sessionId))
        {
            return McpSessionValidation.Unknown;
        }

        var callerKey = principalKey ?? AnonymousPrincipalKey;
        if (_sessions.TryGetValue(sessionId, out var cached))
        {
            if (!string.Equals(cached.PrincipalKey, callerKey, StringComparison.Ordinal))
            {
                // Existence check only: another identity must not keep the session alive.
                try
                {
                    if (!await _store.ExistsAsync(sessionId, cancellationToken).ConfigureAwait(false))
                    {
                        Terminate(sessionId);
                        return McpSessionValidation.Unknown;
                    }
                }
                catch (Exception ex) when (IsStoreFailure(ex))
                {
                    LogStoreFailure(ex, "exists");
                }

                return McpSessionValidation.PrincipalMismatch;
            }

            bool alive;
            try
            {
                alive = await _store.TouchAsync(sessionId, _idleTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                LogStoreFailure(ex, "touch");
                alive = !IsExpired(cached, _timeProvider.GetUtcNow());
            }

            if (!alive)
            {
                Terminate(sessionId);
                return McpSessionValidation.Unknown;
            }

            cached.Touch(_timeProvider.GetUtcNow());
            return McpSessionValidation.Valid;
        }

        McpSessionRecord? record;
        try
        {
            // Read without sliding. A mismatched principal must not keep the session alive.
            record = await _store.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            LogStoreFailure(ex, "read");
            return McpSessionValidation.Unknown;
        }

        if (record is null)
        {
            return McpSessionValidation.Unknown;
        }

        if (!string.Equals(record.PrincipalKey, callerKey, StringComparison.Ordinal))
        {
            return McpSessionValidation.PrincipalMismatch;
        }

        bool sharedAlive;
        try
        {
            sharedAlive = await _store.TouchAsync(sessionId, _idleTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            // The read can be stale by the time the touch fails. Do not adopt it.
            LogStoreFailure(ex, "touch");
            return McpSessionValidation.Unknown;
        }

        if (!sharedAlive)
        {
            return McpSessionValidation.Unknown;
        }

        // Admission has to install the local entry. A removal tombstone or a full
        // anonymous pool both refuse, and a Valid result with no cache entry would
        // drop the negotiated view on the next call.
        if (!Adopt(record))
        {
            return McpSessionValidation.Unknown;
        }

        return McpSessionValidation.Valid;
    }

    /// <summary>
    /// Terminates a session on this instance and, when a shared store is composed,
    /// on every instance: the record is removed from the store and the removal is
    /// broadcast so replicas holding the session end its node-local work at once.
    /// Used by <c>DELETE /mcp</c>. Returns
    /// <see cref="McpSessionTermination.StoreUnavailable"/> when the shared record
    /// could not be removed, so the caller never acknowledges a termination other
    /// replicas would not honour.
    /// </summary>
    public async Task<McpSessionTermination> TerminateAsync(string sessionId, CancellationToken cancellationToken)
    {
        // Local first: the store's removal broadcast also reaches this instance and
        // must not race the result reported here.
        var found = Terminate(sessionId);
        if (_statelessTokens is not null)
        {
            // Nothing outside this process remembers the session, so it cannot be
            // revoked on other instances: report that instead of a termination.
            return McpSessionTermination.NotRevocable;
        }

        if (_store is not null && !string.IsNullOrEmpty(sessionId))
        {
            try
            {
                await _store.RemoveAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                LogStoreFailure(ex, "remove");
                return McpSessionTermination.StoreUnavailable;
            }
        }

        return found ? McpSessionTermination.Terminated : McpSessionTermination.NotFound;
    }

    /// <summary>
    /// Caches a session record read from the shared store. Returns <c>false</c> when
    /// the record was removed, or when admitting it would exceed the anonymous pool
    /// or the session cap without a victim this admission is allowed to displace.
    /// Capacity pressure evicts least-recently-used entries from this instance's
    /// cache only; the store keeps them. The local idle clock is not consulted:
    /// another replica may be refreshing the authoritative expiry, and closing the
    /// node-local channel here would drop a session the store still considers live.
    /// An anonymous record never displaces an authenticated session.
    /// </summary>
    private bool Adopt(McpSessionRecord record)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_capacityGate)
        {
            if (WasRemoved(record.SessionId, now))
            {
                return false;
            }

            if (_sessions.ContainsKey(record.SessionId))
            {
                return true;
            }

            var anonymous = IsAnonymous(record.PrincipalKey);
            var excess = _sessions.Count - _maxSessions + 1;
            if (anonymous)
            {
                excess = Math.Max(
                    excess,
                    _sessions.Values.Count(static session => session.IsAnonymous) - _maxAnonymousSessions + 1);
            }

            if (excess > 0)
            {
                EvictLeastRecentlyUsed(excess, anonymousOnly: anonymous);
                if (_sessions.Count >= _maxSessions
                    || (anonymous && _sessions.Values.Count(static session => session.IsAnonymous) >= _maxAnonymousSessions))
                {
                    return false;
                }
            }

            _sessions[record.SessionId] = new McpSession(
                record.PrincipalKey,
                now,
                record.ElicitationSupported,
                record.WorkflowView);
            return true;
        }
    }

    private static bool IsStoreFailure(Exception ex) =>
        ex is RedisException or TimeoutException or JsonException or ObjectDisposedException;

    private void LogStoreFailure(Exception ex, string operation)
    {
        if (_logger is not null)
        {
            McpLog.SessionStoreUnavailable(_logger, operation, ex);
        }
    }

    /// <summary>
    /// Returns the workflow discovery view negotiated for the session at
    /// <c>initialize</c> (honua-server#3428), or <c>null</c> when the session is
    /// unknown, terminated, idle-expired, or negotiated no view. Does not refresh
    /// the sliding idle window: this is a read of handshake state, not client
    /// activity.
    /// </summary>
    public string? GetWorkflowView(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var session))
        {
            return null;
        }

        return IsExpired(session, _timeProvider.GetUtcNow()) ? null : session.WorkflowView;
    }

    /// <summary>
    /// Returns <c>true</c> when the session identified by <paramref name="sessionId"/>
    /// is active and its client advertised the MCP elicitation capability at
    /// <c>initialize</c> (honua-server#2484). An unknown, terminated, or
    /// idle-expired id — or a session whose client did not advertise elicitation —
    /// reports <c>false</c>. Does not refresh the sliding idle window: this is a
    /// read of state established at handshake, not client activity.
    /// </summary>
    public bool SupportsElicitation(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var session))
        {
            return false;
        }

        return !IsExpired(session, _timeProvider.GetUtcNow()) && session.ElicitationSupported;
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
        if (_statelessTokens is not null)
        {
            return ValidateStatelessAccess(sessionId, principalKey ?? AnonymousPrincipalKey);
        }

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
    /// Validates a stateless session id. The token is authoritative: its signature,
    /// lifetime, and principal binding decide, and the local table only caches the
    /// node-local state (notification channel, lifetime token, job index) of a
    /// session this instance has served. A valid token this instance has never seen
    /// is adopted with the view and elicitation flag it carries.
    /// </summary>
    private McpSessionValidation ValidateStatelessAccess(string sessionId, string callerKey)
    {
        if (!_statelessTokens!.TryRead(sessionId, out var claims, out var binding))
        {
            Terminate(sessionId);
            return McpSessionValidation.Unknown;
        }

        if (!_statelessTokens.IsBoundTo(binding, callerKey))
        {
            return McpSessionValidation.PrincipalMismatch;
        }

        var now = _timeProvider.GetUtcNow();
        if (_sessions.TryGetValue(sessionId, out var cached))
        {
            cached.Touch(now);
            return McpSessionValidation.Valid;
        }

        var record = new McpSessionRecord(
            sessionId,
            callerKey,
            claims.ElicitationSupported,
            claims.WorkflowView,
            claims.CreatedUtc);
        return Adopt(record) ? McpSessionValidation.Valid : McpSessionValidation.Unknown;
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

    private void RememberRemoval(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        lock (_capacityGate)
        {
            _removedSessions[sessionId] = now;
            if (_removedSessions.Count <= _maxSessions)
            {
                return;
            }

            var cutoff = now - _idleTimeout;
            List<string>? stale = null;
            foreach (var pair in _removedSessions)
            {
                if (pair.Value < cutoff)
                {
                    (stale ??= []).Add(pair.Key);
                }
            }

            if (stale is not null)
            {
                foreach (var id in stale)
                {
                    _removedSessions.Remove(id);
                }
            }
        }
    }

    // Caller holds _capacityGate.
    private bool WasRemoved(string sessionId, DateTimeOffset now)
    {
        if (!_removedSessions.TryGetValue(sessionId, out var removedAt))
        {
            return false;
        }

        if (removedAt < now - _idleTimeout)
        {
            _removedSessions.Remove(sessionId);
            return false;
        }

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
        foreach (var pair in _sessions.Where(pair => IsExpired(pair.Value, now)))
        {
            Terminate(pair.Key);
        }
    }

    private static bool IsAnonymous(string principalKey) =>
        string.Equals(principalKey, AnonymousPrincipalKey, StringComparison.Ordinal);

    /// <summary>
    /// Evicts the <paramref name="count"/> least-recently-used sessions, taking
    /// anonymous sessions before authenticated ones and only anonymous sessions
    /// when <paramref name="anonymousOnly"/> is set. Called under
    /// <see cref="_capacityGate"/> after an expiry sweep left the table at
    /// capacity, so an <c>initialize</c> can still be admitted.
    /// </summary>
    private string[] EvictLeastRecentlyUsed(int count, bool anonymousOnly)
    {
        if (count <= 0)
        {
            return [];
        }

        var victims = _sessions
            .Where(pair => !anonymousOnly || pair.Value.IsAnonymous)
            .OrderByDescending(pair => pair.Value.IsAnonymous)
            .ThenBy(pair => pair.Value.LastAccessUtc)
            .Take(count)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var id in victims)
        {
            Terminate(id);
        }

        return victims;
    }

    private static string GenerateSessionId()
    {
        Span<byte> buffer = stackalloc byte[SessionIdByteLength];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexStringLower(buffer);
    }

    private sealed class McpSession
    {
        public McpSession(
            string principalKey,
            DateTimeOffset createdUtc,
            bool elicitationSupported = false,
            string? workflowView = null)
        {
            PrincipalKey = principalKey;
            LastAccessUtc = createdUtc;
            ElicitationSupported = elicitationSupported;
            WorkflowView = workflowView;
        }

        /// <summary>
        /// Stable key of the principal the session was bound to at <c>initialize</c>
        /// (<see cref="AnonymousPrincipalKey"/> for an anonymous session).
        /// </summary>
        public string PrincipalKey { get; }

        /// <summary>Whether the session belongs to the anonymous pool (SEC-18).</summary>
        public bool IsAnonymous => McpSessionManager.IsAnonymous(PrincipalKey);

        /// <summary>
        /// Whether the client advertised the MCP elicitation capability at
        /// <c>initialize</c> (honua-server#2484). Immutable for the session
        /// lifetime — capabilities are negotiated once at handshake.
        /// </summary>
        public bool ElicitationSupported { get; }

        /// <summary>
        /// The workflow discovery view the client negotiated at <c>initialize</c>
        /// (honua-server#3428), or <c>null</c> for the complete catalog. Immutable
        /// for the session lifetime — the view is negotiated once at handshake and
        /// can still be overridden per <c>tools/list</c> call.
        /// </summary>
        public string? WorkflowView { get; }

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
