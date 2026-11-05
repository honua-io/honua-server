// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Temporal.Domain;

// Cancellation is rejected because the in-process work cannot be stopped once it has started.
public sealed record TemporalCorrectiveJobProgress : IOperationProgress
{
    public required string OperationId { get; init; }

    public required OperationStatus Status { get; init; }

    public required string OperationName { get; init; }

    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? CurrentPhase { get; init; }

    public double? PercentComplete => Status == OperationStatus.Completed ? 100 : null;

    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    OperationType IOperationProgress.Type => OperationType.TemporalCorrective;

    public static TemporalCorrectiveJobProgress CreateQueued(string jobId, string operationName, string serviceId, int layerId)
        => new()
        {
            OperationId = jobId,
            Status = OperationStatus.Queued,
            OperationName = operationName,
            ServiceId = serviceId,
            LayerId = layerId,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Queued"
        };
}
