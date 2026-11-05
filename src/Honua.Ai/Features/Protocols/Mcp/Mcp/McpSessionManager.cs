// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using StackExchange.Redis;

namespace Honua.Ai.Protocols.Mcp;

internal enum McpSessionValidation
{
    Valid,

    Unknown,

    PrincipalMismatch
}

internal enum McpSessionTermination
{
    Terminated,

    NotFound,

    NotRevocable,

    StoreUnavailable
}

internal sealed partial class McpSessionManager
{
    public const string SessionHeaderName = "Mcp-Session-Id";

    // The anonymous principal key is distinct so an anonymous caller cannot present an authenticated session.
    public const string AnonymousPrincipalKey = "";

    private const int SessionIdByteLength = 32; // 256 bits.

    // The oldest notification frame is dropped so a slow client cannot grow memory without a bound.
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
    private readonly int _maxSessionsPerPrincipal;
    private readonly TimeSpan _idleTimeout;
    private readonly McpSessionEvictionPolicy _evictionPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly IMcpSessionStore? _store;
    private readonly McpStatelessSessionTokens? _statelessTokens;
    private readonly ILogger? _logger;

    // Held for the manager's (process) lifetime; the multiplexer owns teardown.
    private readonly IDisposable? _removalSubscription;

    public McpSessionManager(
        int maxSessions = 10_000,
        TimeSpan? idleTimeout = null,
        McpSessionEvictionPolicy evictionPolicy = McpSessionEvictionPolicy.EvictLeastRecentlyUsed,
        TimeProvider? timeProvider = null,
        int maxAnonymousSessions = 1_000,
        IMcpSessionStore? store = null,
        ILogger? logger = null,
        McpStatelessSessionTokens? statelessTokens = null,
        int maxSessionsPerPrincipal = McpOptions.DefaultMaxSessionsPerPrincipal)
    {
        _maxSessions = maxSessions > 0 ? maxSessions : 1;
        _maxAnonymousSessions = Math.Clamp(maxAnonymousSessions, 1, _maxSessions);
        var perPrincipal = maxSessionsPerPrincipal > 0
            ? maxSessionsPerPrincipal
            : McpOptions.DefaultMaxSessionsPerPrincipal;
        _maxSessionsPerPrincipal = perPrincipal >= _maxSessions
            ? Math.Max(1, _maxSessions - 1)
            : perPrincipal;
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

    public bool HasSharedStore => _store is not null;

    public McpSessionMode Mode =>
        _store is not null ? McpSessionMode.Shared
        : _statelessTokens is not null ? McpSessionMode.Stateless
        : McpSessionMode.InMemory;

    public bool ReceivesSharedRemovals => _removalSubscription is not null;

    public string CreateSession()
    {
        TryCreateSession(AnonymousPrincipalKey, out var id);
        return id;
    }

    public bool TryCreateSession(string? principalKey, out string sessionId) =>
        TryCreateSession(principalKey, elicitationSupported: false, out sessionId);

    public bool TryCreateSession(string? principalKey, bool elicitationSupported, out string sessionId) =>
        TryCreateSession(principalKey, elicitationSupported, workflowView: null, out sessionId);

    public bool TryCreateSession(
        string? principalKey,
        bool elicitationSupported,
        string? workflowView,
        out string sessionId) =>
        TryCreateSessionCore(principalKey, elicitationSupported, workflowView, out sessionId);

    // With a shared store, capacity eviction drops only this replica's cache entries, because local least-recently-used order is this replica's traffic and must not end a session that is active elsewhere.
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
            if (!TryAdmit(key))
            {
                sessionId = string.Empty;
                return false;
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

    // Termination fails when the shared record cannot be removed, so the caller does not acknowledge an end other replicas would miss.
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

            if (!TryAdmit(record.PrincipalKey))
            {
                return false;
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

    public string? GetWorkflowView(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var session))
        {
            return null;
        }

        return IsExpired(session, _timeProvider.GetUtcNow()) ? null : session.WorkflowView;
    }

    public bool SupportsElicitation(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var session))
        {
            return false;
        }

        return !IsExpired(session, _timeProvider.GetUtcNow()) && session.ElicitationSupported;
    }

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

    public bool Terminate(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !_sessions.TryRemove(sessionId, out var session))
        {
            return false;
        }

        foreach (var jobId in session.Jobs.Keys)
        {
            _jobSessions.TryRemove(new KeyValuePair<string, string>(jobId, sessionId));
        }

        session.Channel.Writer.TryComplete();
        try
        {
            session.Lifetime.Cancel();
        }
        finally
        {
            // Linked provider callbacks can throw. Preserve that failure while
            // still disposing the session-owned cancellation resources.
            session.Lifetime.Dispose();
        }

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

    public CancellationToken GetLifetimeToken(string sessionId) =>
        !string.IsNullOrEmpty(sessionId) && _sessions.TryGetValue(sessionId, out var session)
            ? session.Lifetime.Token
            : CancellationToken.None;

    // Server-initiated notification enqueues do not refresh the idle window, which tracks client activity only.
    public bool TryEnqueue(string sessionId, string payload)
    {
        if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var session))
        {
            return false;
        }

        return session.Channel.Writer.TryWrite(payload);
    }

    public void AssociateJob(string sessionId, string jobId)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(jobId))
        {
            return;
        }

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        session.Jobs[jobId] = 0;
        _jobSessions[jobId] = sessionId;
        if (_sessions.ContainsKey(sessionId))
        {
            return;
        }

        // Terminate won the race after the index insert. Drop the association so
        // the job map does not retain a session that has already ended.
        session.Jobs.TryRemove(jobId, out _);
        _jobSessions.TryRemove(new KeyValuePair<string, string>(jobId, sessionId));
    }

    public bool TryGetJobSession(string jobId, out string sessionId)
    {
        if (!string.IsNullOrEmpty(jobId) && _jobSessions.TryGetValue(jobId, out var resolved))
        {
            if (_sessions.ContainsKey(resolved))
            {
                sessionId = resolved;
                return true;
            }

            _jobSessions.TryRemove(new KeyValuePair<string, string>(jobId, resolved));
        }

        sessionId = string.Empty;
        return false;
    }

    public IReadOnlyCollection<string> ActiveSessionIds => _sessions.Keys.ToArray();

    private bool IsExpired(McpSession session, DateTimeOffset now) =>
        now - session.LastAccessUtc > _idleTimeout;

    private void SweepExpired(DateTimeOffset now)
    {
        foreach (var pair in _sessions.Where(pair => IsExpired(pair.Value, now)))
        {
            Terminate(pair.Key);
        }
    }

    private static bool IsAnonymous(string principalKey) =>
        string.Equals(principalKey, AnonymousPrincipalKey, StringComparison.Ordinal);

    private bool TryAdmit(string principalKey)
    {
        if (IsAnonymous(principalKey))
        {
            var excess = Math.Max(
                _sessions.Count - _maxSessions + 1,
                Count(static session => session.IsAnonymous) - _maxAnonymousSessions + 1);
            if (excess <= 0)
            {
                return true;
            }

            if (_evictionPolicy == McpSessionEvictionPolicy.RejectNew)
            {
                return false;
            }

            EvictLeastRecentlyUsed(excess, anonymousOnly: true);
            return _sessions.Count < _maxSessions;
        }

        var owned = Count(session => string.Equals(session.PrincipalKey, principalKey, StringComparison.Ordinal));
        var principalExcess = owned - _maxSessionsPerPrincipal + 1;
        if (principalExcess > 0)
        {
            if (_evictionPolicy == McpSessionEvictionPolicy.RejectNew)
            {
                return false;
            }

            EvictLeastRecentlyUsed(principalExcess, anonymousOnly: false, onlyPrincipal: principalKey);
            if (Count(session => string.Equals(session.PrincipalKey, principalKey, StringComparison.Ordinal))
                >= _maxSessionsPerPrincipal)
            {
                return false;
            }
        }

        var globalExcess = _sessions.Count - _maxSessions + 1;
        if (globalExcess <= 0)
        {
            return true;
        }

        if (_evictionPolicy == McpSessionEvictionPolicy.RejectNew)
        {
            return false;
        }

        EvictLeastRecentlyUsed(globalExcess, anonymousOnly: true);
        return _sessions.Count < _maxSessions;
    }

    private int Count(Func<McpSession, bool> predicate)
    {
        var count = 0;
        foreach (var session in _sessions.Values.Where(predicate))
        {
            count++;
        }

        return count;
    }

    private string[] EvictLeastRecentlyUsed(int count, bool anonymousOnly, string? onlyPrincipal = null)
    {
        if (count <= 0)
        {
            return [];
        }

        var victims = _sessions
            .Where(pair =>
                (onlyPrincipal is null
                    || string.Equals(pair.Value.PrincipalKey, onlyPrincipal, StringComparison.Ordinal))
                && (!anonymousOnly || pair.Value.IsAnonymous))
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

        public string PrincipalKey { get; }

        public bool IsAnonymous => McpSessionManager.IsAnonymous(PrincipalKey);

        public bool ElicitationSupported { get; }

        public string? WorkflowView { get; }

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
