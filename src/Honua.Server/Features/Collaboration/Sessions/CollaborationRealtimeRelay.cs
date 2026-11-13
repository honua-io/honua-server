// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Threading.Channels;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Core.Features.Realtime;

namespace Honua.Server.Features.Collaboration.Sessions;

// One ordered consumer per family per task. Resolving sessions after construction avoids a DI
// cycle; joins wait for the retained presence snapshot before exposing their initial snapshot.
internal sealed partial class CollaborationRealtimeRelay(
    IRealtimeFanoutProvider provider,
    IServiceProvider services,
    ILogger<CollaborationRealtimeRelay> logger) : BackgroundService
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _operationsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Ready => Task.WhenAll(_ready.Task, _operationsReady.Task);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sessions = services.GetRequiredService<InMemoryCollaborationSessionService>();
        var operations = services.GetRequiredService<ISavedMapOperationLogRepository>();
        await Task.WhenAll(ConsumeAsync(RealtimeFamily.CollabOps), ConsumeAsync(RealtimeFamily.CollabPresence)).ConfigureAwait(false);

        async Task ConsumeAsync(RealtimeFamily family)
        {
            long? after = null;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var window = await provider.GetWindowAsync(family, stoppingToken).ConfigureAwait(false);
                    after ??= family == RealtimeFamily.CollabPresence ? Math.Max(0, window.Oldest - 1) : window.Head;
                    var deliveries = Channel.CreateBounded<RealtimeDelivery>(256);
                    await using var subscription = provider.Subscribe(new RealtimeSubscriptionRequest(family, [], after, deliveries.Writer));
                    if (family == RealtimeFamily.CollabOps)
                    {
                        _operationsReady.TrySetResult();
                    }

                    if (family == RealtimeFamily.CollabPresence && after >= window.Head)
                    {
                        _ready.TrySetResult();
                    }

                    await foreach (var delivery in deliveries.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
                    {
                        if (delivery is RealtimeDelivery.Reset)
                        {
                            sessions.RequireResync(clearPresence: family == RealtimeFamily.CollabPresence);
                            after = null;
                            break;
                        }

                        if (delivery is not RealtimeDelivery.Event(var value))
                        {
                            continue;
                        }

                        if (family == RealtimeFamily.CollabPresence)
                        {
                            var message = value.Payload.Deserialize(CollaborationSessionJsonContext.Default.CollaborationPresenceMessage);
                            if (message is not null)
                            {
                                sessions.ApplyRemotePresence(message);
                            }
                        }
                        else
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

                        after = value.Sequence;
                        if (family == RealtimeFamily.CollabPresence && after >= window.Head)
                        {
                            _ready.TrySetResult();
                        }
                    }
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
                    sessions.RequireResync(clearPresence: family == RealtimeFamily.CollabPresence);
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 7441, Level = LogLevel.Warning,
            Message = "Collaboration realtime delivery failed for {Family}; reconnecting from the last delivered position.")]
        public static partial void DeliveryFailed(ILogger logger, string family, Exception exception);
    }
}
