// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;

namespace Honua.Infrastructure.Abstractions;

internal sealed record SubscriptionOptions(string? ClientLabel = null);

internal interface IAlertNotificationSubscription : IDisposable
{
    Guid SubscriberId { get; }

    CancellationToken DisconnectToken { get; }
}

internal interface IAlertNotificationBroadcaster
{
    Task BroadcastAsync(AlertEventEnvelope alertEvent, CancellationToken cancellationToken = default);

    IAlertNotificationSubscription Subscribe(Func<AlertEventEnvelope, CancellationToken, Task> handler);

    IAlertNotificationSubscription Subscribe(Func<AlertEventEnvelope, CancellationToken, Task> handler, SubscriptionOptions? options);
}
