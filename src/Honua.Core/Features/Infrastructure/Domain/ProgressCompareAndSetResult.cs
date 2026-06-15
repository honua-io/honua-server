// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// Outcome of a conditional (compare-and-set) progress write against
/// <see cref="Abstractions.IUniversalProgressStore"/>.
/// </summary>
public enum ProgressCompareAndSetOutcome
{
    /// <summary>
    /// The stored progress had the expected status and was replaced.
    /// </summary>
    Updated,

    /// <summary>
    /// No progress record exists (or it expired) for the operation; nothing was written.
    /// </summary>
    NotFound,

    /// <summary>
    /// The stored progress no longer has the expected status; nothing was written.
    /// </summary>
    StatusMismatch
}

/// <summary>
/// Result of a conditional (compare-and-set) progress write. When the write is rejected with
/// <see cref="ProgressCompareAndSetOutcome.StatusMismatch"/>, <see cref="CurrentProgress"/> carries the
/// progress observed at decision time so callers can react to the winning transition (for example,
/// surfacing a conflict when a worker persisted a terminal state before a cancel could be applied).
/// </summary>
public sealed record ProgressCompareAndSetResult
{
    /// <summary>
    /// Outcome of the conditional write.
    /// </summary>
    public required ProgressCompareAndSetOutcome Outcome { get; init; }

    /// <summary>
    /// The progress observed when the write was rejected with
    /// <see cref="ProgressCompareAndSetOutcome.StatusMismatch"/>; <c>null</c> for other outcomes.
    /// </summary>
    public IOperationProgress? CurrentProgress { get; init; }

    /// <summary>
    /// Shared result instance for a successful conditional update.
    /// </summary>
    public static ProgressCompareAndSetResult Updated { get; } = new() { Outcome = ProgressCompareAndSetOutcome.Updated };

    /// <summary>
    /// Shared result instance for a missing (or expired) progress record.
    /// </summary>
    public static ProgressCompareAndSetResult NotFound { get; } = new() { Outcome = ProgressCompareAndSetOutcome.NotFound };

    /// <summary>
    /// Creates a status-mismatch result carrying the progress observed at decision time.
    /// </summary>
    /// <param name="currentProgress">The progress stored when the conditional write was rejected.</param>
    public static ProgressCompareAndSetResult StatusMismatch(IOperationProgress currentProgress)
        => new() { Outcome = ProgressCompareAndSetOutcome.StatusMismatch, CurrentProgress = currentProgress };
}
