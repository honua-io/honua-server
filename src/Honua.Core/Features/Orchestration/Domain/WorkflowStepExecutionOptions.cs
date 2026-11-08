// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Orchestration.Domain;

public abstract record WorkflowStepExecutionOptions
{
    public bool HasExecutionSettings()
        => RetryPolicy is not null || FailurePolicy != WorkflowStepFailurePolicy.Fail
            || TimeoutSeconds is not null || Condition is not null || ForEach is not null;

    public StepRetryPolicy? RetryPolicy { get; init; }

    public WorkflowStepFailurePolicy FailurePolicy { get; init; } = WorkflowStepFailurePolicy.Fail;

    public int? TimeoutSeconds { get; init; }

    public WorkflowStepCondition? Condition { get; init; }

    public WorkflowForEachSpec? ForEach { get; init; }
}
