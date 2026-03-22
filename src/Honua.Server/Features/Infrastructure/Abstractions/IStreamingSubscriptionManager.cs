// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Abstractions;

/// <summary>
/// Metadata about an active streaming subscription exposed for admin visibility.
/// </summary>
internal sealed record StreamingSubscriptionInfo(Guid SubscriberId, DateTimeOffset ConnectedAt, string? ClientLabel);

/// <summary>
/// Admin-facing abstraction for querying and managing streaming subscriptions.
/// Implemented by the alert notification broadcaster but exposed through Infrastructure
/// to maintain vertical slice isolation.
/// </summary>
internal interface IStreamingSubscriptionManager
{
    /// <summary>
    /// Returns a snapshot of all active subscriptions.
    /// </summary>
    IReadOnlyList<StreamingSubscriptionInfo> GetSubscriptions();

    /// <summary>
    /// Force-disconnects a subscriber by ID.
    /// </summary>
    /// <returns>True if the subscriber was found and removed, false otherwise.</returns>
    bool DisconnectSubscriber(Guid subscriberId);
}
