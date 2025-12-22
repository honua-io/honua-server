// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.FeatureServer.Models;

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Validates and applies limits to feature query parameters
/// </summary>
/// <summary>
/// Result of query parameter validation
/// </summary>
public sealed record QueryValidationResult
{
    /// <summary>
    /// Whether the validation was successful
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// The validated parameters (only set when IsValid is true)
    /// </summary>
    public QueryParameters? ValidatedParameters { get; init; }

    /// <summary>
    /// Error message explaining why validation failed (only set when IsValid is false)
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a successful validation result
    /// </summary>
    public static QueryValidationResult Success(QueryParameters validatedParameters) =>
        new() { IsValid = true, ValidatedParameters = validatedParameters };

    /// <summary>
    /// Creates a failed validation result
    /// </summary>
    public static QueryValidationResult Failure(string errorMessage) =>
        new() { IsValid = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Result of related records query parameter validation
/// </summary>
public sealed record RelatedRecordsValidationResult
{
    /// <summary>
    /// Whether the validation was successful
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// The validated parameters (only set when IsValid is true)
    /// </summary>
    public QueryRelatedRecordsParameters? ValidatedParameters { get; init; }

    /// <summary>
    /// Error message explaining why validation failed (only set when IsValid is false)
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a successful validation result
    /// </summary>
    public static RelatedRecordsValidationResult Success(QueryRelatedRecordsParameters validatedParameters) =>
        new() { IsValid = true, ValidatedParameters = validatedParameters };

    /// <summary>
    /// Creates a failed validation result
    /// </summary>
    public static RelatedRecordsValidationResult Failure(string errorMessage) =>
        new() { IsValid = false, ErrorMessage = errorMessage };
}

public interface IFeatureQueryValidator
{
    /// <summary>
    /// Validates and applies query limits to the provided parameters
    /// </summary>
    /// <param name="queryParams">The query parameters to validate</param>
    /// <returns>Validation result with either validated parameters or error message</returns>
    QueryValidationResult ValidateQueryLimits(QueryParameters queryParams);

    /// <summary>
    /// Validates and applies related records query limits to the provided parameters
    /// </summary>
    /// <param name="queryParams">The related records query parameters to validate</param>
    /// <returns>Validation result with either validated parameters or error message</returns>
    RelatedRecordsValidationResult ValidateRelatedRecordsLimits(QueryRelatedRecordsParameters queryParams);
}
