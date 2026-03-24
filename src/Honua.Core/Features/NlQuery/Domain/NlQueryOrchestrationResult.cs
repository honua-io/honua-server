// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.NlQuery.Domain;

/// <summary>
/// Result of the end-to-end NL query orchestration: plan generation + compilation.
/// </summary>
public sealed class NlQueryOrchestrationResult
{
    private NlQueryOrchestrationResult(
        bool isSuccess,
        FilterExpression? expression,
        FilterPlan? plan,
        string? errorMessage,
        NlQueryOrchestrationStage failedStage)
    {
        IsSuccess = isSuccess;
        Expression = expression;
        Plan = plan;
        ErrorMessage = errorMessage;
        FailedStage = failedStage;
    }

    /// <summary>
    /// Whether the full pipeline succeeded.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// The compiled filter expression AST. Non-null when <see cref="IsSuccess"/> is true.
    /// </summary>
    public FilterExpression? Expression { get; }

    /// <summary>
    /// The intermediate filter plan produced by the provider. Available on success
    /// or when compilation (not planning) failed.
    /// </summary>
    public FilterPlan? Plan { get; }

    /// <summary>
    /// Error message when the pipeline failed.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// The stage at which failure occurred, if any.
    /// </summary>
    public NlQueryOrchestrationStage FailedStage { get; }

    /// <summary>
    /// Creates a successful orchestration result.
    /// </summary>
    public static NlQueryOrchestrationResult Success(FilterExpression expression, FilterPlan plan) =>
        new(true, expression, plan, null, NlQueryOrchestrationStage.None);

    /// <summary>
    /// Creates a failed result at the plan generation stage.
    /// </summary>
    public static NlQueryOrchestrationResult PlanGenerationFailed(string errorMessage) =>
        new(false, null, null, errorMessage, NlQueryOrchestrationStage.PlanGeneration);

    /// <summary>
    /// Creates a failed result at the plan compilation stage.
    /// </summary>
    public static NlQueryOrchestrationResult CompilationFailed(string errorMessage, FilterPlan plan) =>
        new(false, null, plan, errorMessage, NlQueryOrchestrationStage.Compilation);
}

/// <summary>
/// Stages of the NL query orchestration pipeline.
/// </summary>
public enum NlQueryOrchestrationStage
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>Plan generation from the NL provider failed.</summary>
    PlanGeneration,

    /// <summary>Compilation of the filter plan into a filter expression failed.</summary>
    Compilation
}
