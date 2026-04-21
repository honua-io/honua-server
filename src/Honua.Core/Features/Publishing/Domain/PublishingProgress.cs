// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Publishing.Domain;

/// <summary>
/// Progress tracking for a publishing operation, implementing the unified operation
/// progress interface.
/// </summary>
public sealed record PublishingProgress : IOperationProgress, ICancellableOperationProgress
{
    /// <summary>
    /// Unique identifier for this publishing operation.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Current publish intent lifecycle status.
    /// </summary>
    public required PublishIntentStatus IntentStatus { get; init; }

    /// <summary>
    /// Identifier of the publish intent being executed.
    /// </summary>
    public string? IntentId { get; init; }

    /// <summary>
    /// Identifier of the resulting published service when available.
    /// </summary>
    public string? ServiceId { get; init; }

    /// <summary>
    /// Progress percentage (0-100), null if total is unknown.
    /// </summary>
    public double? PercentComplete { get; init; }

    /// <summary>
    /// When the operation started.
    /// </summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// When the operation completed (null if still running).
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Duration of the operation.
    /// </summary>
    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    /// <summary>
    /// Error message if the operation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Non-fatal warnings encountered during the operation.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Current processing phase description.
    /// </summary>
    public string? CurrentPhase { get; init; }

    /// <summary>
    /// Unified operation status projected from <see cref="IntentStatus"/>.
    /// </summary>
    public OperationStatus Status => IntentStatus switch
    {
        PublishIntentStatus.Draft => OperationStatus.Queued,
        PublishIntentStatus.Validated => OperationStatus.Queued,
        PublishIntentStatus.AwaitingApproval => OperationStatus.Queued,
        PublishIntentStatus.Approved => OperationStatus.Queued,
        PublishIntentStatus.Executing => OperationStatus.Processing,
        PublishIntentStatus.Completed => OperationStatus.Completed,
        PublishIntentStatus.Rejected => OperationStatus.Failed,
        PublishIntentStatus.Failed => OperationStatus.Failed,
        PublishIntentStatus.Cancelled => OperationStatus.Cancelled,
        _ => OperationStatus.Queued
    };

    // IOperationProgress explicit implementation
    string IOperationProgress.OperationId => OperationId;
    OperationType IOperationProgress.Type => OperationType.Publishing;

    /// <inheritdoc />
    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            IntentStatus = PublishIntentStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

    /// <summary>
    /// Creates an initial progress record for a new publishing operation.
    /// </summary>
    public static PublishingProgress CreateInitial(string operationId, string intentId)
        => new()
        {
            OperationId = operationId,
            IntentStatus = PublishIntentStatus.Draft,
            IntentId = intentId,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Initializing"
        };

    /// <summary>
    /// Creates a progress record for an executing publishing operation.
    /// </summary>
    public static PublishingProgress CreateExecuting(string operationId, string intentId)
        => new()
        {
            OperationId = operationId,
            IntentStatus = PublishIntentStatus.Executing,
            IntentId = intentId,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Publishing"
        };
}
