// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.NlQuery.Domain;

public sealed class NlQueryPlanResult
{
    private NlQueryPlanResult(bool isSuccess, FilterPlan? plan, string? errorMessage)
    {
        IsSuccess = isSuccess;
        Plan = plan;
        ErrorMessage = errorMessage;
    }

    public bool IsSuccess { get; }

    public FilterPlan? Plan { get; }

    public string? ErrorMessage { get; }

    public static NlQueryPlanResult Success(FilterPlan plan) => new(true, plan, null);

    public static NlQueryPlanResult Failure(string errorMessage) => new(false, null, errorMessage);
}
