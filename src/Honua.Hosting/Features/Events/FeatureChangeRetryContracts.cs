// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Events;

/// <summary>
/// Contract surface for the feature-change retry queue. The interface and
/// pending-publish records live in Honua.Hosting so the JSON source-gen
/// context (<see cref="FeatureChangeEventsJsonContext"/>) and the hosted
/// background service can compile against them without a back-edge to
/// Server. The concrete <c>FeatureChangeRetryQueue</c> implementation
/// remains in Honua.Server because its constructor takes a
/// <c>Honua.Server.Features.Streaming.FeatureStreamSessionManager</c>,
/// and the Streaming sub-area has not been carved into Hosting (see
/// ADR-0044 for the deferral).
/// </summary>
internal interface IFeatureChangeRetryQueue
{
    Task<string> EnqueueAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<string> ReadQueuedIdsAsync(CancellationToken cancellationToken = default);
    Task ProcessQueuedAsync(string pendingId, CancellationToken cancellationToken = default);
}

internal sealed record PendingFeatureChangePublish
{
    public required string PendingId { get; init; }
    public required FeatureChangeEventRequest Request { get; init; }
    public required DateTimeOffset EnqueuedAt { get; init; }
    public int AttemptCount { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public bool BroadcastCompleted { get; init; }
}

internal sealed record PendingFeatureChangeIndex
{
    public string[] PendingIds { get; init; } = [];
}

internal readonly record struct PendingFeatureChangeSignal(string PendingId);
