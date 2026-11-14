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
            var latest = new Dictionary<Guid, RealtimeEvent>();
            var after = 0L;
            while (after < window.Head)
            {
                var page = await provider.ReadPresencePageAsync(mapId, after, window.Head, cancellationToken).ConfigureAwait(false);
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
            sessions.RequireResync(clearPresence: false);
            return;
        }

        await _presenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _snapshotHeads.Clear();
            sessions.RequireResync(clearPresence: true);
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

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sessions = services.GetRequiredService<InMemoryCollaborationSessionService>();
        var operations = services.GetRequiredService<ISavedMapOperationLogRepository>();
        return Task.WhenAll(
            ConsumeAsync(RealtimeFamily.CollabOps, sessions, operations, stoppingToken),
            ConsumeAsync(RealtimeFamily.CollabPresence, sessions, operations, stoppingToken));
    }

    private async Task ConsumeAsync(RealtimeFamily family, InMemoryCollaborationSessionService sessions,
        ISavedMapOperationLogRepository operations, CancellationToken stoppingToken)
    {
        long? after = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var window = await provider.GetWindowAsync(family, stoppingToken).ConfigureAwait(false);
                after ??= window.Head;
                var deliveries = Channel.CreateBounded<RealtimeDelivery>(256);
                await using var subscription = provider.Subscribe(new RealtimeSubscriptionRequest(family, [], after, deliveries.Writer));
                if (family == RealtimeFamily.CollabOps)
                {
                    _operationsReady.TrySetResult();
                }
                else
                {
                    await MarkPresenceReadyAsync(after.Value, stoppingToken).ConfigureAwait(false);
                }

                after = await ReadDeliveriesAsync(family, deliveries.Reader, after, sessions, operations, stoppingToken).ConfigureAwait(false);
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
                    after = null;
                }
                await ResetAsync(sessions, family, stoppingToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<long?> ReadDeliveriesAsync(RealtimeFamily family, ChannelReader<RealtimeDelivery> deliveries,
        long? after, InMemoryCollaborationSessionService sessions, ISavedMapOperationLogRepository operations,
        CancellationToken stoppingToken)
    {
        await foreach (var delivery in deliveries.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (delivery is RealtimeDelivery.Reset)
            {
                await ResetAsync(sessions, family, stoppingToken).ConfigureAwait(false);
                return null;
            }

            if (delivery is not RealtimeDelivery.Event(var value))
            {
                continue;
            }

            if (family == RealtimeFamily.CollabPresence)
            {
                await DeliverPresenceAsync(sessions, value, stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await DeliverOperationAsync(sessions, operations, value, stoppingToken).ConfigureAwait(false);
            }
            after = value.Sequence;
        }
        return after;
    }

    private static async Task DeliverOperationAsync(InMemoryCollaborationSessionService sessions,
        ISavedMapOperationLogRepository operations, RealtimeEvent value, CancellationToken stoppingToken)
    {
        var cursor = value.Payload.GetProperty("serverCursor").GetInt64();
        var id = value.Payload.GetProperty("operationId").GetString();
        var replay = await operations.ReplayAsync(new SavedMapId(value.ScopeKey),
            new SavedMapOperationCursor(cursor - 1), stoppingToken).ConfigureAwait(false);
        var operation = replay.Operations.FirstOrDefault(op => op.ServerCursor.Value == cursor && op.OperationId.Value == id);
        if (operation is null)
        {
            sessions.RequireResync(clearPresence: false);
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
