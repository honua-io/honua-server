// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.NlQuery.Domain;

/// <summary>
/// Result of an NL query plan generation, carrying either a <see cref="FilterPlan"/> or an error.
/// </summary>
public sealed class NlQueryPlanResult
{
    private NlQueryPlanResult(bool isSuccess, FilterPlan? plan, string? errorMessage)
    {
        IsSuccess = isSuccess;
        Plan = plan;
        ErrorMessage = errorMessage;
    }

    /// <summary>
    /// Whether the plan was generated successfully.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// The generated filter plan. Non-null when <see cref="IsSuccess"/> is true.
    /// </summary>
    public FilterPlan? Plan { get; }

    /// <summary>
    /// Error message when plan generation failed.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Creates a successful result with the given plan.
    /// </summary>
    public static NlQueryPlanResult Success(FilterPlan plan) => new(true, plan, null);

    /// <summary>
    /// Creates a failed result with the given error message.
    /// </summary>
    public static NlQueryPlanResult Failure(string errorMessage) => new(false, null, errorMessage);
}
