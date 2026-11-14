// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Honua.Core.Features.Realtime;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.SensorThings.Streaming;

internal sealed record ObservationStreamRequest(
    string Transport, long? DatastreamId, ObservationStreamScope Scope, string? PrincipalId, long? After);

internal sealed class ObservationStreamSessionManager : IAsyncDisposable
{
    internal const string MeterName = "Honua.SensorThings";
    private const string UntenantedPartition = "untenanted";
    private const string UnidentifiedPrincipal = "unidentified";
    private readonly ConcurrentDictionary<Guid, SessionEntry> _sessions = new();
    private readonly ILogger<ObservationStreamSessionManager> _logger;
    private readonly IRealtimeFanoutProvider _provider;
    private readonly Meter _meter = new(MeterName);
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
        IRealtimeFanoutProvider provider,
        ObservationStreamOptions? options = null)
    {
        _logger = logger;
        _provider = provider;
        _options = options ?? new ObservationStreamOptions();
        _observationStreamDrops = _meter.CreateCounter<long>(
            "honua_sensorthings_observation_stream_drops_total",
            description: "Observation batches overflowing a stream buffer.");
        _sessionRejections = _meter.CreateCounter<long>(
            "honua_sensorthings_observation_stream_rejections_total",
            description: "Observation subscriptions refused by admission limits.");
    }

    public long SlowConsumerDrops => Interlocked.Read(ref _slowConsumerDrops);
    internal Meter Meter => _meter;
    internal ObservationStreamOptions Options => _options;
    public int SessionCount => Volatile.Read(ref _activeSessionCount);

    public ObservationStreamSession? TryCreateSession(
        ObservationStreamRequest request, out ObservationStreamAdmissionLimit rejectedBy)
    {
        ArgumentNullException.ThrowIfNull(request.Scope);
        var tenant = string.IsNullOrWhiteSpace(request.Scope.TenantId)
            ? UntenantedPartition : "tenant:" + request.Scope.TenantId;
        var principal = (tenant, string.IsNullOrWhiteSpace(request.PrincipalId)
            ? UnidentifiedPrincipal : request.PrincipalId);
        rejectedBy = TryReserveSlot(tenant, principal);
        if (rejectedBy != ObservationStreamAdmissionLimit.None)
        {
            _sessionRejections.Add(1,
                new KeyValuePair<string, object?>("limit", rejectedBy.ToString()),
                new KeyValuePair<string, object?>("transport", request.Transport));
            ObservationStreamLog.SessionRejected(_logger, request.Transport, rejectedBy);
            return null;
        }

        return CreateReservedSession(request, principal);
    }

    private ObservationStreamSession CreateReservedSession(
        ObservationStreamRequest request, (string Tenant, string Principal) principal)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<RealtimeDelivery>(new BoundedChannelOptions(_options.MaxBufferPerConnection)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        var state = new ObservationStreamSessionState();
        var writer = new DeliveryWriter(channel.Writer,
            () => DisconnectSlowConsumer(new(id, request.DatastreamId, state, cts)));
        try
        {
            var subscription = _provider.Subscribe(new RealtimeSubscriptionRequest(
                RealtimeFamily.Observations,
                request.DatastreamId is { } datastream ? [datastream.ToString(CultureInfo.InvariantCulture)] : [],
                request.After, writer, value => MatchesScope(value, request.Scope)));
            _sessions[id] = new(subscription, channel.Writer, cts, principal, state);
        }
        catch
        {
            ReleaseSlot(principal);
            cts.Dispose();
            throw;
        }

        ObservationStreamLog.SessionCreated(_logger, id, request.Transport, request.DatastreamId);
        return new ObservationStreamSession(id, channel.Reader, this, state, token);
    }

    private sealed record SlowConsumerState(
        Guid Id, long? DatastreamId, ObservationStreamSessionState State, CancellationTokenSource Cancellation);

    private void DisconnectSlowConsumer(SlowConsumerState consumer)
    {
        if (consumer.State.TrySet(ObservationStreamDisconnectReason.SlowConsumer))
        {
            Interlocked.Increment(ref _slowConsumerDrops);
            _observationStreamDrops.Add(1,
                new KeyValuePair<string, object?>("datastream.id", consumer.DatastreamId));
            ObservationStreamLog.SlowConsumerDisconnected(_logger, consumer.Id);
            consumer.Cancellation.Cancel();
        }
    }

    private static bool MatchesScope(RealtimeEvent value, ObservationStreamScope scope)
    {
        return value.Kind == "observation.created"
            && value.Payload.ValueKind == JsonValueKind.Object
            && value.Payload.TryGetProperty("scope", out var stored)
            && stored.ValueKind == JsonValueKind.Object
            && Matches(stored, "tenantId", scope.TenantId)
            && Matches(stored, "schema", scope.Schema);

        static bool Matches(JsonElement stored, string name, string? expected) =>
            stored.TryGetProperty(name, out var field)
            && (field.ValueKind == JsonValueKind.Null ? expected is null
                : field.ValueKind == JsonValueKind.String && field.GetString() == expected);
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

    internal async ValueTask RemoveSessionAsync(Guid id)
    {
        if (!_sessions.TryRemove(id, out var entry))
        {
            return;
        }

        entry.State.TrySet(ObservationStreamDisconnectReason.ClientClosed);
        ReleaseSlot(entry.Principal);
        try
        {
            await entry.Cts.CancelAsync().ConfigureAwait(false);
            await entry.Subscription.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            entry.Writer.TryComplete();
            entry.Cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // Start every cleanup even if one provider subscription fails to dispose.
            await Task.WhenAll(_sessions.Keys.Select(id => RemoveSessionAsync(id).AsTask()))
                .ConfigureAwait(false);
        }
        finally
        {
            _meter.Dispose();
        }
    }

    private sealed record SessionEntry(
        IRealtimeSubscription Subscription,
        ChannelWriter<RealtimeDelivery> Writer,
        CancellationTokenSource Cts,
        (string Tenant, string Principal) Principal,
        ObservationStreamSessionState State);

    private sealed class DeliveryWriter(ChannelWriter<RealtimeDelivery> inner, Action overflow)
        : ChannelWriter<RealtimeDelivery>
    {
        public override bool TryWrite(RealtimeDelivery item)
        {
            if (inner.TryWrite(item))
            {
                return true;
            }

            overflow();
            return false;
        }

        public override ValueTask WriteAsync(RealtimeDelivery item, CancellationToken cancellationToken = default)
            => inner.WriteAsync(item, cancellationToken);

        public override bool TryComplete(Exception? error = null) => inner.TryComplete(error);
        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
            => inner.WaitToWriteAsync(cancellationToken);
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

internal sealed class ObservationStreamSession : IAsyncDisposable
{
    private readonly ObservationStreamSessionManager _manager;

    private readonly ObservationStreamSessionState _state;

    public ObservationStreamSession(
        Guid sessionId,
        ChannelReader<RealtimeDelivery> reader,
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

    public ChannelReader<RealtimeDelivery> Reader { get; }

    public CancellationToken DisconnectToken { get; }

    public ObservationStreamDisconnectReason DisconnectReason => _state.Reason;

    public ValueTask DisposeAsync() => _manager.RemoveSessionAsync(SessionId);
}
