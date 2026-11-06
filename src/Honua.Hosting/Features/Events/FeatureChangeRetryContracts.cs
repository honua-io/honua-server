// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Events;

// The contract lives here so Hosting can compile against it, and the queue implementation stays in the server project because it depends on a streaming type this assembly cannot reference.
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
