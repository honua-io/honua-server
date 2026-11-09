// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;

namespace Honua.Core.Features.Alerts.Abstractions;

public interface IAlertDeliverySink
{
    AlertChannelType ChannelType { get; }

    Task<AlertDeliveryResult> DeliverAsync(
        AlertDispatchItem dispatchItem,
        AlertEventEnvelope alertEvent,
        CancellationToken cancellationToken = default);
}

public sealed record AlertDeliveryResult
{
    public required bool Succeeded { get; init; }

    public required bool Retryable { get; init; }

    public string? Error { get; init; }
}
