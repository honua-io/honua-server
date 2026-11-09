// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Orchestration.Domain;

public sealed record WorkflowStepDefinition
{
    public required string StepId { get; init; }

    public required AnalysisPlan Plan { get; init; }

    public IReadOnlyList<string> DependsOn { get; init; } = [];

    public IReadOnlyList<StepInputBinding> InputBindings { get; init; } = [];

    public StepRetryPolicy? RetryPolicy { get; init; }

    public WorkflowStepFailurePolicy FailurePolicy { get; init; } = WorkflowStepFailurePolicy.Fail;

    public int? TimeoutSeconds { get; init; }

    public WorkflowStepCondition? Condition { get; init; }

    public WorkflowForEachSpec? ForEach { get; init; }
}
