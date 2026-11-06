// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Publishing.Domain;

public sealed record PublishingProgress : IOperationProgress, ICancellableOperationProgress
{
    public required string OperationId { get; init; }

    public required PublishIntentStatus IntentStatus { get; init; }

    public string? IntentId { get; init; }

    public string? ServiceId { get; init; }

    public double? PercentComplete { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? CurrentPhase { get; init; }

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

    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            IntentStatus = PublishIntentStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

    public static PublishingProgress CreateInitial(string operationId, string intentId)
        => new()
        {
            OperationId = operationId,
            IntentStatus = PublishIntentStatus.Draft,
            IntentId = intentId,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Initializing"
        };

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
