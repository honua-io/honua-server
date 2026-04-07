// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Events;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Decorates <see cref="IFeatureChangeEventPublisher"/> to fan out events to live
/// streaming sessions after durable append. This ensures clients never miss events
/// between replay completion and live delivery.
/// </summary>
internal sealed partial class FeatureStreamPublisher(
    IFeatureChangeEventStore store,
    FeatureStreamSessionManager sessionManager,
    ILogger<FeatureStreamPublisher> logger,
    IFeatureChangeRetryQueue? retryQueue = null) : IFeatureChangeEventPublisher
{
    private readonly IFeatureChangeEventStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly FeatureStreamSessionManager _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
    private readonly ILogger<FeatureStreamPublisher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IFeatureChangeRetryQueue? _retryQueue = retryQueue;

    public async Task PublishAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
    {
        var durableRequest = string.IsNullOrWhiteSpace(request.EventId)
            ? request with { EventId = Guid.NewGuid().ToString("N") }
            : request;

        FeatureChangeEvent persisted;
        try
        {
            persisted = await _store.AppendAsync(durableRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogPublishCancelled(_logger);
            await QueueRetryAsync(durableRequest).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            LogPublishFailed(_logger, ex);
            await QueueRetryAsync(durableRequest).ConfigureAwait(false);
            return;
        }

        // Fan out to live streaming sessions after durable append.
        // Enrichment data (geometry envelope + properties) is carried on the message
        // for subscription filter evaluation during broadcast — no I/O in the hot path.
        var envelope = ToEnvelope(persisted);
        var delivered = _sessionManager.Broadcast(
            FeatureStreamMessage.Data(envelope, persisted.GeometryEnvelope, persisted.PropertiesJson));

        FeatureStreamLog.EventBroadcast(_logger, delivered, persisted.Cursor);
    }

    internal static FeatureStreamEnvelope ToEnvelope(FeatureChangeEvent e) => new()
    {
        EventId = e.EventId,
        Cursor = e.Cursor,
        Timestamp = e.Timestamp,
        ServiceId = e.ServiceId,
        LayerId = e.LayerId,
        ObjectId = e.ObjectId,
        Operation = e.Operation,
        Protocol = e.Protocol,
        RequestId = e.RequestId,
        ChangedAttributes = e.ChangedAttributes,
        GeometryChanged = e.GeometryChanged
    };

    [LoggerMessage(EventId = 5100, Level = LogLevel.Warning, Message = "Failed to publish feature-change event to store.")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5101, Level = LogLevel.Warning, Message = "Feature-change event publish was cancelled after the originating write completed.")]
    private static partial void LogPublishCancelled(ILogger logger);

    [LoggerMessage(EventId = 5102, Level = LogLevel.Warning, Message = "Failed to queue feature-change publish retry.")]
    private static partial void LogRetryQueueFailed(ILogger logger, Exception exception);

    private async Task QueueRetryAsync(FeatureChangeEventRequest request)
    {
        if (_retryQueue == null)
        {
            return;
        }

        try
        {
            await _retryQueue.EnqueueAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRetryQueueFailed(_logger, ex);
        }
    }
}
