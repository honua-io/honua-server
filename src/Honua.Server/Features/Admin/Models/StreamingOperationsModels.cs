// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Response listing all active streaming subscribers.
/// </summary>
internal sealed class SubscriberListResponse
{
    /// <summary>
    /// Total number of active subscribers.
    /// </summary>
    public int SubscriberCount { get; init; }

    /// <summary>
    /// Details of each active subscriber.
    /// </summary>
    public SubscriberInfoResponse[] Subscribers { get; init; } = [];

    /// <summary>
    /// Timestamp when this response was generated.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>
/// Details of a single active streaming subscriber.
/// </summary>
internal sealed class SubscriberInfoResponse
{
    /// <summary>
    /// Unique identifier for the subscriber.
    /// </summary>
    public Guid SubscriberId { get; init; }

    /// <summary>
    /// Timestamp when the subscriber connected.
    /// </summary>
    public DateTimeOffset ConnectedAt { get; init; }

    /// <summary>
    /// Optional client label set at subscribe time.
    /// </summary>
    public string? ClientLabel { get; init; }

    /// <summary>
    /// Duration of the subscription in seconds.
    /// </summary>
    public double DurationSeconds { get; init; }
}
