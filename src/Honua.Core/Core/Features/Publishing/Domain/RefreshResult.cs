// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Publishing.Domain;

public sealed record RefreshResult
{
    public required string RequestId { get; init; }

    public required string ServiceId { get; init; }

    public required RefreshOutcome Outcome { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    public int ItemsUpdated { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static RefreshResult CreateSucceeded(
        string requestId,
        string serviceId,
        DateTimeOffset startedAt,
        int itemsUpdated,
        IReadOnlyList<string>? warnings = null)
        => new()
        {
            RequestId = requestId,
            ServiceId = serviceId,
            Outcome = RefreshOutcome.Succeeded,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            ItemsUpdated = itemsUpdated,
            Warnings = warnings ?? []
        };

    public static RefreshResult CreateFailed(
        string requestId,
        string serviceId,
        DateTimeOffset startedAt,
        IReadOnlyList<string> errors,
        int itemsUpdated = 0)
        => new()
        {
            RequestId = requestId,
            ServiceId = serviceId,
            Outcome = RefreshOutcome.Failed,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            ItemsUpdated = itemsUpdated,
            Errors = errors
        };
}
