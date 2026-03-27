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
    ILogger<FeatureStreamPublisher> logger) : IFeatureChangeEventPublisher
{
    private readonly IFeatureChangeEventStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly FeatureStreamSessionManager _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
    private readonly ILogger<FeatureStreamPublisher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task PublishAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
    {
        FeatureChangeEvent persisted;
        try
        {
            persisted = await _store.AppendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogPublishFailed(_logger, ex);
            return;
        }

        // Fan out to live streaming sessions after durable append.
        var envelope = ToEnvelope(persisted);
        _sessionManager.Broadcast(FeatureStreamMessage.Data(envelope));

        FeatureStreamLog.EventBroadcast(_logger, _sessionManager.SessionCount, persisted.Cursor);
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
        RequestId = e.RequestId
    };

    [LoggerMessage(EventId = 5100, Level = LogLevel.Warning, Message = "Failed to publish feature-change event to store.")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception);
}
