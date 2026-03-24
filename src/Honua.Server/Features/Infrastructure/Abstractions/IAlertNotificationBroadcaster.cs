// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;

namespace Honua.Server.Features.Infrastructure.Abstractions;

/// <summary>
/// Options for creating a subscription with metadata.
/// </summary>
internal sealed record SubscriptionOptions(string? ClientLabel = null);

/// <summary>
/// Handle for a live alert notification subscription.
/// </summary>
internal interface IAlertNotificationSubscription : IDisposable
{
    Guid SubscriberId { get; }

    CancellationToken DisconnectToken { get; }
}

/// <summary>
/// Shared abstraction for publishing alert notifications to live subscribers.
/// Admin endpoints consume this via Infrastructure to avoid a direct dependency
/// on the Alerts feature implementation.
/// </summary>
internal interface IAlertNotificationBroadcaster
{
    /// <summary>
    /// Broadcasts an alert event to all connected subscribers.
    /// </summary>
    Task BroadcastAsync(AlertEventEnvelope alertEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a subscriber callback. Dispose the returned handle to unsubscribe.
    /// </summary>
    IAlertNotificationSubscription Subscribe(Func<AlertEventEnvelope, CancellationToken, Task> handler);

    /// <summary>
    /// Registers a subscriber callback with metadata. Dispose the returned handle to unsubscribe.
    /// </summary>
    IAlertNotificationSubscription Subscribe(Func<AlertEventEnvelope, CancellationToken, Task> handler, SubscriptionOptions? options);
}
