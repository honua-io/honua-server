// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.Orchestration.Domain;

public sealed record WorkflowRun
{
    public required string RunId { get; init; }

    public required string WorkflowId { get; init; }

    public required WorkflowRunStatus Status { get; init; }

    public OperationPriority Priority { get; init; } = OperationPriority.Normal;

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public required IReadOnlyList<WorkflowStepState> StepStates { get; init; }

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public OperationAuditInfo Audit { get; init; } = new();

    public WorkflowTriggerKind TriggerKind { get; init; } = WorkflowTriggerKind.Manual;

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
