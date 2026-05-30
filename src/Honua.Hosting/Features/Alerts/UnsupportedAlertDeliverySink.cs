// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Alerts;

internal sealed class UnsupportedAlertDeliverySink : IAlertDeliverySink
{
    public UnsupportedAlertDeliverySink(AlertChannelType channelType)
    {
        ChannelType = channelType;
    }

    public AlertChannelType ChannelType { get; }

    public Task<AlertDeliveryResult> DeliverAsync(
        AlertDispatchItem dispatchItem,
        AlertEventEnvelope alertEvent,
        CancellationToken cancellationToken = default)
    {
        _ = dispatchItem;
        _ = alertEvent;
        _ = cancellationToken;

        return Task.FromResult(new AlertDeliveryResult
        {
            Succeeded = false,
            Retryable = false,
            Error = $"Channel '{ChannelType}' is not implemented."
        });
    }
}
