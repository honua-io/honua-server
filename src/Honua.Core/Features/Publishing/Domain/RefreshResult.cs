// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Publishing.Domain;

/// <summary>
/// Outcome of a one-way refresh operation that pulled source content into a published service.
/// </summary>
public sealed record RefreshResult
{
    /// <summary>
    /// Identifier of the refresh request that produced this result.
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// Identifier of the published service that was refreshed.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Outcome of the refresh operation.
    /// </summary>
    public required RefreshOutcome Outcome { get; init; }

    /// <summary>
    /// When the refresh operation started.
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// When the refresh operation completed.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Duration of the refresh operation.
    /// </summary>
    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    /// <summary>
    /// Number of items updated during the refresh.
    /// </summary>
    public int ItemsUpdated { get; init; }

    /// <summary>
    /// Error messages encountered during the refresh.
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>
    /// Non-fatal warning messages encountered during the refresh.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Creates a successful refresh result.
    /// </summary>
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

    /// <summary>
    /// Creates a failed refresh result.
    /// </summary>
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
