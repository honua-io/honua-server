// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.NlQuery.Domain;

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

    public bool IsSuccess { get; }

    public FilterExpression? Expression { get; }

    public FilterPlan? Plan { get; }

    public string? ErrorMessage { get; }

    public NlQueryOrchestrationStage FailedStage { get; }

    public static NlQueryOrchestrationResult Success(FilterExpression expression, FilterPlan plan) =>
        new(true, expression, plan, null, NlQueryOrchestrationStage.None);

    public static NlQueryOrchestrationResult PlanGenerationFailed(string errorMessage) =>
        new(false, null, null, errorMessage, NlQueryOrchestrationStage.PlanGeneration);

    public static NlQueryOrchestrationResult CompilationFailed(string errorMessage, FilterPlan plan) =>
        new(false, null, plan, errorMessage, NlQueryOrchestrationStage.Compilation);
}

public enum NlQueryOrchestrationStage
{
    None,

    PlanGeneration,

    Compilation
}
