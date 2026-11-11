// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Honua.Core.Features.SensorThings.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.SensorThings.Streaming;

internal sealed class ObservationStreamSessionManager : IDisposable
{
    // This meter name must be on the service-defaults allow-list or the drop counter is excluded from export.
    internal const string MeterName = "Honua.SensorThings";

    private const string UntenantedPartition = "untenanted";
    private const string UnidentifiedPrincipal = "unidentified";

    private readonly ConcurrentDictionary<Guid, SessionEntry> _sessions = new();
    private readonly ILogger<ObservationStreamSessionManager> _logger;
    private readonly Meter _meter;
    private readonly Counter<long> _observationStreamDrops;
    private readonly Counter<long> _sessionRejections;
    private readonly ObservationStreamOptions _options;
    private readonly Lock _admissionLock = new();
    private readonly Dictionary<string, int> _tenantSessionCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Tenant, string Principal), int> _principalSessionCounts = new();
    private int _activeSessionCount;
    private long _slowConsumerDrops;

    public ObservationStreamSessionManager(
        ILogger<ObservationStreamSessionManager> logger,
        ObservationStreamOptions? options = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new ObservationStreamOptions();
        _meter = new Meter(MeterName);
        _observationStreamDrops = _meter.CreateCounter<long>(
            "honua_sensorthings_observation_stream_drops_total",
            description: "Observation-stream frames that overflowed a session buffer and disconnected that session.");
        _sessionRejections = _meter.CreateCounter<long>(
            "honua_sensorthings_observation_stream_rejections_total",
            description: "Observation-stream sessions refused because a principal, tenant, or node admission cap was reached.");
    }

    public long SlowConsumerDrops => Interlocked.Read(ref _slowConsumerDrops);

    internal Meter Meter => _meter;

    internal ObservationStreamOptions Options => _options;

    public int SessionCount => Volatile.Read(ref _activeSessionCount);

    public ObservationStreamSession? TryCreateSession(
        string transport, long? datastreamId, ObservationStreamScope scope, string? principalId = null)
        => TryCreateSession(transport, datastreamId, scope, principalId, out _);

    public ObservationStreamSession? TryCreateSession(
        string transport,
        long? datastreamId,
        ObservationStreamScope scope,
        string? principalId,
        out ObservationStreamAdmissionLimit rejectedBy)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var tenant = string.IsNullOrWhiteSpace(scope.TenantId) ? UntenantedPartition : "tenant:" + scope.TenantId;
        var principal = (tenant, string.IsNullOrWhiteSpace(principalId) ? UnidentifiedPrincipal : principalId);
        rejectedBy = TryReserveSlot(tenant, principal);
        if (rejectedBy != ObservationStreamAdmissionLimit.None)
        {
            _sessionRejections.Add(1,
                new KeyValuePair<string, object?>("limit", rejectedBy.ToString()),
                new KeyValuePair<string, object?>("transport", transport));
            ObservationStreamLog.SessionRejected(_logger, transport, rejectedBy);
            return null;
        }

        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<ObservationStreamFrame>(new BoundedChannelOptions(_options.MaxBufferPerConnection)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        var cts = new CancellationTokenSource();
        // Capture before the session is visible. Overflow on another thread can remove
        // it and dispose this source before the return, and Token then throws.
        var disconnectToken = cts.Token;
        var state = new ObservationStreamSessionState();
        var entry = new SessionEntry(channel, cts, datastreamId, transport, scope, principal, state);
        if (!_sessions.TryAdd(id, entry))
        {
            ReleaseSlot(principal);
            cts.Dispose();
            rejectedBy = ObservationStreamAdmissionLimit.Node;
            return null;
        }

        ObservationStreamLog.SessionCreated(_logger, id, transport, datastreamId);
        return new ObservationStreamSession(id, channel.Reader, this, state, disconnectToken);
    }

    private ObservationStreamAdmissionLimit TryReserveSlot(string tenant, (string Tenant, string Principal) principal)
    {
        lock (_admissionLock)
        {
            // Most specific first: a caller over its own quota is told so (429) even when the
            // node is also saturated, because only its own disconnects can admit it.
            if (_principalSessionCounts.GetValueOrDefault(principal) >= _options.MaxSessionsPerPrincipal)
            {
                return ObservationStreamAdmissionLimit.Principal;
            }

            if (_tenantSessionCounts.GetValueOrDefault(tenant) >= _options.MaxSessionsPerTenant)
            {
                return ObservationStreamAdmissionLimit.Tenant;
            }

            if (_activeSessionCount >= _options.MaxConcurrentSessions)
            {
                return ObservationStreamAdmissionLimit.Node;
            }

            _principalSessionCounts[principal] = _principalSessionCounts.GetValueOrDefault(principal) + 1;
            _tenantSessionCounts[tenant] = _tenantSessionCounts.GetValueOrDefault(tenant) + 1;
            Volatile.Write(ref _activeSessionCount, _activeSessionCount + 1);
            return ObservationStreamAdmissionLimit.None;
        }
    }

    private void ReleaseSlot((string Tenant, string Principal) principal)
    {
        lock (_admissionLock)
        {
            Decrement(_principalSessionCounts, principal);
            Decrement(_tenantSessionCounts, principal.Tenant);
            Volatile.Write(ref _activeSessionCount, _activeSessionCount - 1);
        }

        static void Decrement<TKey>(Dictionary<TKey, int> counts, TKey key)
            where TKey : notnull
        {
            if (!counts.TryGetValue(key, out var count))
            {
                return;
            }

            if (count <= 1)
            {
                counts.Remove(key);
            }
            else
            {
                counts[key] = count - 1;
            }
        }
    }

    public void RemoveSession(Guid sessionId)
        => RemoveSession(sessionId, ObservationStreamDisconnectReason.ClientClosed);

    private void RemoveSession(Guid sessionId, ObservationStreamDisconnectReason reason)
    {
        if (!_sessions.TryRemove(sessionId, out var entry))
        {
            return;
        }

        entry.State.TrySet(reason);
        if (reason == ObservationStreamDisconnectReason.SlowConsumer)
        {
            ObservationStreamLog.SlowConsumerDisconnected(_logger, sessionId);
        }

        ReleaseSlot(entry.Principal);
        entry.Cts.Cancel();
        entry.Cts.Dispose();
    }

    public void PublishObservations(IReadOnlyList<SensorThingsObservation> observations, ObservationStreamScope scope)
    {
        if (observations is null || observations.Count == 0)
        {
            return;
        }

        foreach (var frame in observations.Select(ObservationStreamFrame.FromObservation))
        {
            Broadcast(frame, scope);
        }
    }

    private void Broadcast(ObservationStreamFrame frame, ObservationStreamScope scope)
    {
        List<Guid>? overflow = null;
        foreach (var (id, entry) in _sessions)
        {
            if (entry.Cts.IsCancellationRequested || entry.Scope != scope)
            {
                continue;
            }

            if (entry.DatastreamId is { } datastreamId && datastreamId != frame.DatastreamId)
            {
                continue;
            }

            // Wait-mode bounded channel with non-blocking TryWrite: a full buffer
            // reports false. Count the lost frame, then disconnect that session so
            // the transport can emit a gap instead of leaving it looking healthy.
            if (!entry.Channel.Writer.TryWrite(frame))
            {
                NoteOverflow(id, frame.DatastreamId, ref overflow);
            }
        }

        DisconnectOverflow(overflow);
    }

    public void BroadcastHeartbeat()
    {
        List<Guid>? overflow = null;
        foreach (var (id, entry) in _sessions)
        {
            if (entry.Cts.IsCancellationRequested)
            {
                continue;
            }

            if (!entry.Channel.Writer.TryWrite(ObservationStreamFrame.Heartbeat()))
            {
                NoteOverflow(id, entry.DatastreamId, ref overflow);
            }
        }

        DisconnectOverflow(overflow);
    }

    private void NoteOverflow(Guid sessionId, long? datastreamId, ref List<Guid>? overflow)
    {
        Interlocked.Increment(ref _slowConsumerDrops);
        _observationStreamDrops.Add(1, new KeyValuePair<string, object?>("datastream.id", datastreamId));
        overflow ??= new List<Guid>();
        overflow.Add(sessionId);
    }

    private void DisconnectOverflow(List<Guid>? overflow)
    {
        if (overflow is null)
        {
            return;
        }

        foreach (var sessionId in overflow)
        {
            RemoveSession(sessionId, ObservationStreamDisconnectReason.SlowConsumer);
        }
    }

    public void Dispose()
    {
        foreach (var (_, entry) in _sessions)
        {
            entry.Cts.Cancel();
            entry.Cts.Dispose();
        }

        _sessions.Clear();
        lock (_admissionLock)
        {
            _principalSessionCounts.Clear();
            _tenantSessionCounts.Clear();
            Volatile.Write(ref _activeSessionCount, 0);
        }

        _meter.Dispose();
    }

    private sealed class SessionEntry
    {
        public SessionEntry(
            Channel<ObservationStreamFrame> channel,
            CancellationTokenSource cts,
            long? datastreamId,
            string transport,
            ObservationStreamScope scope,
            (string Tenant, string Principal) principal,
            ObservationStreamSessionState state)
        {
            Channel = channel;
            Cts = cts;
            DatastreamId = datastreamId;
            Transport = transport;
            Scope = scope;
            Principal = principal;
            State = state;
        }

        public Channel<ObservationStreamFrame> Channel { get; }
        public CancellationTokenSource Cts { get; }
        public long? DatastreamId { get; }
        public string Transport { get; }
        public ObservationStreamScope Scope { get; }
        public (string Tenant, string Principal) Principal { get; }
        public ObservationStreamSessionState State { get; }
    }
}

internal enum ObservationStreamDisconnectReason
{
    None = 0,

    ClientClosed = 1,

    SlowConsumer = 2,
}

internal sealed class ObservationStreamSessionState
{
    private int _reason;

    public ObservationStreamDisconnectReason Reason
        => (ObservationStreamDisconnectReason)Volatile.Read(ref _reason);

    public bool TrySet(ObservationStreamDisconnectReason reason)
        => Interlocked.CompareExchange(ref _reason, (int)reason, (int)ObservationStreamDisconnectReason.None)
            == (int)ObservationStreamDisconnectReason.None;
}

internal enum ObservationStreamAdmissionLimit
{
    None,

    Principal,

    Tenant,

    Node
}

internal sealed class ObservationStreamSession : IDisposable
{
    private readonly ObservationStreamSessionManager _manager;

    private readonly ObservationStreamSessionState _state;

    public ObservationStreamSession(
        Guid sessionId,
        ChannelReader<ObservationStreamFrame> reader,
        ObservationStreamSessionManager manager,
        ObservationStreamSessionState state,
        CancellationToken disconnectToken)
    {
        SessionId = sessionId;
        Reader = reader;
        DisconnectToken = disconnectToken;
        _manager = manager;
        _state = state;
    }

    public Guid SessionId { get; }

    public ChannelReader<ObservationStreamFrame> Reader { get; }

    public CancellationToken DisconnectToken { get; }

    public ObservationStreamDisconnectReason DisconnectReason => _state.Reason;

    public void Dispose() => _manager.RemoveSession(SessionId);
}
