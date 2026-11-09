// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Orchestration.Domain;

public sealed record WorkflowStepState
{
    public required string StepId { get; init; }

    public required string PlanId { get; init; }

    public required WorkflowStepStatus Status { get; init; }

    public string? JobId { get; init; }

    // Reconciliation prefers the plan authorized at run creation; a ForEach step has no layer binding until it is expanded.
    public AnalysisPlan? AuthorizedPlan { get; init; }

    public int AttemptCount { get; init; }

    public DateTimeOffset? NextAttemptAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public bool TimeoutCancellationRequested { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? ErrorMessage { get; init; }

    public IReadOnlyDictionary<string, string>? ResolvedInputs { get; init; }

    public IReadOnlyList<ArtifactRef>? OutputArtifacts { get; init; }
}
