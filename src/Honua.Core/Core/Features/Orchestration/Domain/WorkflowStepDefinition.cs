// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Orchestration.Domain;

public sealed record WorkflowStepDefinition : WorkflowStepExecutionOptions
{
    public required string StepId { get; init; }

    public required AnalysisPlan Plan { get; init; }

    public IReadOnlyList<string> DependsOn { get; init; } = [];

    public IReadOnlyList<StepInputBinding> InputBindings { get; init; } = [];

    public IReadOnlyList<string> FailureDependsOn { get; init => field = value ?? []; } = [];
}
