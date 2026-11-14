// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Alerts;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Infrastructure.Abstractions;

internal sealed record SubscriptionOptions(string? ClientLabel = null);

internal interface IAlertNotificationBroadcaster
{
    Task BroadcastAsync(AlertEventEnvelope alertEvent, CancellationToken cancellationToken = default);

    InMemoryAlertNotificationBroadcaster.Subscription Subscribe(Func<AlertEventEnvelope, CancellationToken, Task> handler);

    InMemoryAlertNotificationBroadcaster.Subscription Subscribe(Func<AlertEventEnvelope, CancellationToken, Task> handler, SubscriptionOptions? options);
}
