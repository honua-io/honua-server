// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Threading.Channels;
using Honua.Core.Features.Realtime;
using Honua.Infrastructure.Events;

namespace Honua.Server.Features.Streaming;

// This task's one subscription to the features family (ADR-0085). Each change, committed by any
// task or directly in SQL, goes to the session manager, which filters and authorizes it per
// session. If the provider closes the subscription, it resumes after the last change it relayed,
// so nothing committed meanwhile is skipped.
internal sealed partial class FeatureStreamRealtimeRelay(
    IRealtimeFanoutProvider provider,
    FeatureStreamSessionManager sessionManager,
    ILogger<FeatureStreamRealtimeRelay> logger) : BackgroundService
{
    private const int Capacity = 4_096;
    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(1);

    private long? _relayed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RelayAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogRelayFailed(logger, _relayed ?? 0, exception);
                await Task.Delay(_retryDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RelayAsync(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<RealtimeDelivery>(
            new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        await using var subscription = provider.Subscribe(
            new RealtimeSubscriptionRequest(RealtimeFamily.Features, [], _relayed, channel.Writer));
        await foreach (var delivery in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (delivery)
            {
                case RealtimeDelivery.Event { Value: var change }:
                    await RelayAsync(change, cancellationToken).ConfigureAwait(false);
                    break;
                case RealtimeDelivery.Reset reset:
                    LogRelayReset(logger, reset.Reason, reset.Head);
                    _relayed = reset.Head;
                    break;
            }
        }
    }

    private async Task RelayAsync(RealtimeEvent change, CancellationToken cancellationToken)
    {
        var featureChange = change.Payload.Deserialize(FeatureChangeEventsJsonContext.Default.FeatureChangeEvent)
            ?? throw new JsonException($"Feature change {change.Sequence} has no payload.");
        if (await sessionManager.RefreshRoutabilityAsync(cancellationToken).ConfigureAwait(false))
        {
            sessionManager.Broadcast(FeatureStreamMessage.Data(
                FeatureStreamPublisher.ToEnvelope(featureChange),
                featureChange.GeometryEnvelope,
                featureChange.PropertiesJson));
        }

        _relayed = change.Sequence;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The feature-stream relay failed after generation {Generation}; it resumes from there.")]
    private static partial void LogRelayFailed(ILogger logger, long generation, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The feature-stream relay was reset ({Reason}); it continues after generation {Head}.")]
    private static partial void LogRelayReset(ILogger logger, string reason, long head);
}
