// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Alerts;

internal sealed class WebSocketAlertDeliverySink : IAlertDeliverySink
{
    // The dispatcher builds every sink when the host starts, including hosts without PostgreSQL,
    // so the realtime log is resolved on the first delivery rather than at construction.
    private readonly Lazy<RealtimeAlertNotificationBroadcaster> _broadcaster;

    public WebSocketAlertDeliverySink(RealtimeAlertNotificationBroadcaster broadcaster)
    {
        ArgumentNullException.ThrowIfNull(broadcaster);
        _broadcaster = new Lazy<RealtimeAlertNotificationBroadcaster>(broadcaster);
    }

    public WebSocketAlertDeliverySink(Lazy<RealtimeAlertNotificationBroadcaster> broadcaster)
    {
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
    }

    public AlertChannelType ChannelType => AlertChannelType.WebSocket;

    public async Task<AlertDeliveryResult> DeliverAsync(
        AlertDispatchItem dispatchItem,
        AlertEventEnvelope alertEvent,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _broadcaster.Value.BroadcastAsync(alertEvent, cancellationToken).ConfigureAwait(false);

            return new AlertDeliveryResult { Succeeded = true, Retryable = false };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            // Intentional catch-all: this is the delivery-sink boundary for a single alert
            // dispatch attempt; the failure is mapped to a retryable delivery result below
            // rather than propagating and failing the whole dispatch batch.
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = true,
                Error = "WebSocket delivery failed."
            };
        }
    }
}
