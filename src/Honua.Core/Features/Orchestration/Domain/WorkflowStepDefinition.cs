// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Orchestration.Domain;

/// <summary>
/// Declarative definition of a single step inside a workflow DAG.
/// </summary>
public sealed record WorkflowStepDefinition
{
    /// <summary>
    /// Step identifier unique within the owning workflow.
    /// </summary>
    public required string StepId { get; init; }

    /// <summary>
    /// Canonical analysis plan submitted when this step is reached. Its inputs may be
    /// overridden per attempt using <see cref="InputBindings"/>.
    /// </summary>
    public required AnalysisPlan Plan { get; init; }

    /// <summary>
    /// Step identifiers that must be terminal before this step may be submitted.
    /// </summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>
    /// Artifact-to-input bindings applied before the analysis plan is submitted.
    /// </summary>
    public IReadOnlyList<StepInputBinding> InputBindings { get; init; } = [];

    /// <summary>
    /// Optional retry policy. Null disables retries beyond the first attempt.
    /// </summary>
    public StepRetryPolicy? RetryPolicy { get; init; }

    /// <summary>
    /// Behavior applied once the step exhausts its retry budget.
    /// </summary>
    public WorkflowStepFailurePolicy FailurePolicy { get; init; } = WorkflowStepFailurePolicy.Fail;

    /// <summary>
    /// Optional wall-clock timeout for the step's underlying job, in seconds.
    /// </summary>
    public int? TimeoutSeconds { get; init; }
}
