// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.NlQuery.Domain;

/// <summary>
/// Result of compiling a <see cref="FilterPlan"/> into a <see cref="FilterExpression"/> AST.
/// </summary>
public sealed class FilterPlanCompileResult
{
    private FilterPlanCompileResult(bool isSuccess, FilterExpression? expression, string? errorMessage)
    {
        IsSuccess = isSuccess;
        Expression = expression;
        ErrorMessage = errorMessage;
    }

    /// <summary>
    /// Whether compilation succeeded.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// The compiled filter expression AST. Non-null when <see cref="IsSuccess"/> is true.
    /// </summary>
    public FilterExpression? Expression { get; }

    /// <summary>
    /// Error message when compilation failed.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Creates a successful compilation result.
    /// </summary>
    public static FilterPlanCompileResult Success(FilterExpression expression) => new(true, expression, null);

    /// <summary>
    /// Creates a failed compilation result.
    /// </summary>
    public static FilterPlanCompileResult Failure(string errorMessage) => new(false, null, errorMessage);
}
