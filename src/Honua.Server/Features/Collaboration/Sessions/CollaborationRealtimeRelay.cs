// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Threading.Channels;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Core.Features.Realtime;

namespace Honua.Server.Features.Collaboration.Sessions;

// One ordered consumer per family per task. Resolving sessions after construction avoids a DI
// cycle; authorized joins read retained presence independently of subscription replay limits.
internal sealed partial class CollaborationRealtimeRelay(
    IRealtimeFanoutProvider provider,
    IServiceProvider services,
    ILogger<CollaborationRealtimeRelay> logger) : BackgroundService
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _operationsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _presenceGate = new(1, 1);
    private readonly Dictionary<string, long> _snapshotHeads = new(StringComparer.Ordinal);
    private long _presencePosition;
    private const int MaxSnapshotParticipants = 10_000;
    internal Task Ready => Task.WhenAll(_ready.Task, _operationsReady.Task);

    internal async Task ReadPresenceSnapshotAsync(string mapId, CancellationToken cancellationToken)
    {
        await _presenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var window = await provider.GetWindowAsync(RealtimeFamily.CollabPresence, cancellationToken).ConfigureAwait(false);
            var latest = await ReadLatestPresenceAsync(mapId, window.Head, cancellationToken).ConfigureAwait(false);

            // Live delivery shares this gate. Only the last retained state per session is applied,
            // so an earlier page cannot roll back an update already delivered by the live tail.
            var sessions = services.GetRequiredService<InMemoryCollaborationSessionService>();
            foreach (var value in latest.Values.OrderBy(value => value.Sequence))
            {
                ApplyPresence(sessions, value);
            }

            if (window.Head > _presencePosition)
            {
                _snapshotHeads[mapId] = window.Head;
            }
        }
        finally
        {
            _presenceGate.Release();
        }
    }

    private async Task<Dictionary<Guid, RealtimeEvent>> ReadLatestPresenceAsync(string mapId, long head, CancellationToken cancellationToken)
    {
        var latest = new Dictionary<Guid, RealtimeEvent>();
        var after = 0L;
        while (after < head)
        {
            var page = await provider.ReadPresencePageAsync(mapId, after, head, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }
            foreach (var value in page)
            {
                var message = value.Payload.Deserialize(CollaborationSessionJsonContext.Default.CollaborationPresenceMessage);
                if (message is not null && Guid.TryParse(message.Event.SessionId, out var session))
                {
                    latest[session] = value;
                    if (latest.Count > MaxSnapshotParticipants)
                    {
                        throw new InvalidOperationException("The retained collaboration snapshot exceeds participant capacity.");
                    }
                }
            }
            after = page[^1].Sequence;
        }
        return latest;
    }

    private static void ApplyPresence(InMemoryCollaborationSessionService sessions, RealtimeEvent value)
    {
        var message = value.Payload.Deserialize(CollaborationSessionJsonContext.Default.CollaborationPresenceMessage);
        if (message is not null)
        {
            sessions.ApplyRemotePresence(message);
        }
    }

    private async Task DeliverPresenceAsync(InMemoryCollaborationSessionService sessions, RealtimeEvent value, CancellationToken cancellationToken)
    {
        await _presenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_snapshotHeads.TryGetValue(value.ScopeKey, out var head) || value.Sequence > head)
            {
                ApplyPresence(sessions, value);
            }
            _presencePosition = value.Sequence;
            foreach (var map in _snapshotHeads.Where(pair => pair.Value <= value.Sequence).Select(pair => pair.Key).ToArray())
            {
                _snapshotHeads.Remove(map);
            }
        }
        finally
        {
            _presenceGate.Release();
        }
    }

    private async Task ResetAsync(InMemoryCollaborationSessionService sessions, RealtimeFamily family, CancellationToken cancellationToken)
    {
        if (family != RealtimeFamily.CollabPresence)
        {
            sessions.RequireResync();
            return;
        }

        await _presenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _snapshotHeads.Clear();
            sessions.ResetRemotePresence();
        }
        finally
        {
            _presenceGate.Release();
        }
    }

    private async Task MarkPresenceReadyAsync(long head, CancellationToken cancellationToken)
    {
        await _presenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _presencePosition = head;
            _ready.TrySetResult();
        }
        finally
        {
            _presenceGate.Release();
        }
    }

    private InMemoryCollaborationSessionService Sessions => services.GetRequiredService<InMemoryCollaborationSessionService>();

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        ConsumeAsync(RealtimeFamily.CollabOps, stoppingToken),
        ConsumeAsync(RealtimeFamily.CollabPresence, stoppingToken));

    private sealed class DeliveryPosition
    {
        public long? After { get; set; }
    }

    private sealed record DeliveryBatch(RealtimeFamily Family, ChannelReader<RealtimeDelivery> Reader, DeliveryPosition Position);

    private async Task ConsumeAsync(RealtimeFamily family, CancellationToken stoppingToken)
    {
        var position = new DeliveryPosition();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var window = await provider.GetWindowAsync(family, stoppingToken).ConfigureAwait(false);
                position.After ??= window.Head;
                var deliveries = Channel.CreateBounded<RealtimeDelivery>(256);
                await using var subscription = provider.Subscribe(new RealtimeSubscriptionRequest(family, [], position.After, deliveries.Writer));
                if (family == RealtimeFamily.CollabOps)
                {
                    _operationsReady.TrySetResult();
                }
                else
                {
                    await MarkPresenceReadyAsync(position.After.Value, stoppingToken).ConfigureAwait(false);
                }

                await ReadDeliveriesAsync(new DeliveryBatch(family, deliveries.Reader, position), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.DeliveryFailed(logger, family.Name, ex);
                if (family == RealtimeFamily.CollabPresence)
                {
                    position.After = null;
                }
                await ResetAsync(Sessions, family, stoppingToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ReadDeliveriesAsync(DeliveryBatch batch, CancellationToken stoppingToken)
    {
        var (family, deliveries, position) = batch;
        await foreach (var delivery in deliveries.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (delivery is RealtimeDelivery.Reset(_, var head))
            {
                // The reset boundary predates the resync notice. A later window head can
                // include operations committed after a client has already resynchronized.
                position.After = head;
                await ResetAsync(Sessions, family, stoppingToken).ConfigureAwait(false);
                return;
            }

            if (delivery is not RealtimeDelivery.Event(var value))
            {
                continue;
            }

            if (family == RealtimeFamily.CollabPresence)
            {
                await DeliverPresenceAsync(Sessions, value, stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await DeliverOperationAsync(value, stoppingToken).ConfigureAwait(false);
            }
            position.After = value.Sequence;
        }
    }

    private async Task DeliverOperationAsync(RealtimeEvent value, CancellationToken stoppingToken)
    {
        var sessions = Sessions;
        var operations = services.GetRequiredService<ISavedMapOperationLogRepository>();
        var cursor = value.Payload.GetProperty("serverCursor").GetInt64();
        var id = value.Payload.GetProperty("operationId").GetString();
        var replay = await operations.ReplayAsync(new SavedMapId(value.ScopeKey),
            new SavedMapOperationCursor(cursor - 1), stoppingToken).ConfigureAwait(false);
        var operation = replay.Operations.FirstOrDefault(op => op.ServerCursor.Value == cursor && op.OperationId.Value == id);
        if (operation is null)
        {
            sessions.RequireResync();
        }
        else
        {
            sessions.PublishOperation(value.ScopeKey, CollaborationOperationWire.FromEnvelope(operation));
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 7441, Level = LogLevel.Warning,
            Message = "Collaboration realtime delivery failed for {Family}; reconnecting from the last delivered position.")]
        public static partial void DeliveryFailed(ILogger logger, string family, Exception exception);
    }
}
