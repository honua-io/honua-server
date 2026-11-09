// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.NlQuery.Domain;

public sealed class FilterPlanCompileResult
{
    private FilterPlanCompileResult(bool isSuccess, FilterExpression? expression, string? errorMessage)
    {
        IsSuccess = isSuccess;
        Expression = expression;
        ErrorMessage = errorMessage;
    }

    public bool IsSuccess { get; }

    public FilterExpression? Expression { get; }

    public string? ErrorMessage { get; }

    public static FilterPlanCompileResult Success(FilterExpression expression) => new(true, expression, null);

    public static FilterPlanCompileResult Failure(string errorMessage) => new(false, null, errorMessage);
}
