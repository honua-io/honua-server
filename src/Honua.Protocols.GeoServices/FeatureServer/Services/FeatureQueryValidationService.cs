// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Service for validating feature query parameters and limits.
/// Extracted from FeatureServerQueryHandler to improve separation of concerns.
/// </summary>
internal sealed class FeatureQueryValidationService
{
    /// <summary>
    /// Validates query parameters for basic constraints
    /// </summary>
    public static QueryValidationResult ValidateBasicParameters(QueryParameters queryParams)
    {
        var errors = new List<string>();

        // Validate control characters in WHERE clause
        if (!string.IsNullOrEmpty(queryParams.Where) && queryParams.Where.Contains('\0'))
        {
            errors.Add("WHERE clause contains invalid control characters");
        }

        // Validate result record count
        if (queryParams.ResultRecordCount is < 1)
        {
            errors.Add($"{nameof(QueryParameters.ResultRecordCount)} must be greater than 0");
        }

        // Validate result offset
        if (queryParams.ResultOffset is < 0)
        {
            errors.Add($"{nameof(QueryParameters.ResultOffset)} must be 0 or greater");
        }

        if (queryParams.GeometryPrecision is < 0)
        {
            errors.Add($"{nameof(QueryParameters.GeometryPrecision)} must be 0 or greater");
        }

        if (queryParams.MaxAllowableOffset is < 0)
        {
            errors.Add($"{nameof(QueryParameters.MaxAllowableOffset)} must be 0 or greater");
        }

        if (errors.Count > 0)
        {
            return QueryValidationResult.Invalid(string.Join("; ", errors));
        }

        return QueryValidationResult.Valid(queryParams);
    }

    /// <summary>
    /// Validates output format parameters
    /// </summary>
    public static FormatValidationResult ValidateOutputFormat(string? format)
    {
        if (string.IsNullOrEmpty(format))
        {
            return FormatValidationResult.Valid("json");
        }

        return FormatValidationResult.Valid(format);
    }

    /// <summary>
    /// Validates spatial reference system identifiers
    /// </summary>
    public static SridValidationResult ValidateSpatialReference(string? srValue, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(srValue))
        {
            return SridValidationResult.Valid(null);
        }

        // This is a basic validation - full SRID resolution happens elsewhere
        var trimmed = srValue.Trim();

        // Check for obvious invalid patterns
        if (trimmed.Length > 1000) // Reasonable limit for SRID strings
        {
            return SridValidationResult.Invalid($"{parameterName} value is too long");
        }

        return SridValidationResult.Valid(trimmed);
    }

    /// <summary>
    /// Validates related records query parameters
    /// </summary>
    public static RelatedRecordsValidationResult ValidateRelatedRecordsParameters(QueryRelatedRecordsParameters queryParams)
    {
        var errors = new List<string>();

        if (queryParams.ObjectIds.Length == 0)
        {
            errors.Add("objectIds parameter is required");
        }

        if (errors.Count > 0)
        {
            return RelatedRecordsValidationResult.Invalid(string.Join("; ", errors));
        }

        return RelatedRecordsValidationResult.Valid(queryParams);
    }

    /// <summary>
    /// Validates edit operation limits
    /// </summary>
    public static EditLimitsValidationResult ValidateEditLimits(
        ApplyEditsRequest request,
        Honua.Core.Configuration.EditLimits editLimits)
    {
        var addCount = request.Adds?.Length ?? 0;
        var updateCount = request.Updates?.Length ?? 0;
        var deleteCount = request.Deletes?.Length ?? 0;
        var totalCount = addCount + updateCount + deleteCount;

        if (addCount > editLimits.MaxFeaturesPerEdit ||
            updateCount > editLimits.MaxFeaturesPerEdit ||
            deleteCount > editLimits.MaxFeaturesPerEdit)
        {
            return EditLimitsValidationResult.Invalid(
                "Too many features in a single edit operation",
                $"Maximum per operation: {editLimits.MaxFeaturesPerEdit}");
        }

        if (totalCount > editLimits.MaxEditsPerTransaction)
        {
            return EditLimitsValidationResult.Invalid(
                "Too many edits in a single request",
                $"Maximum per request: {editLimits.MaxEditsPerTransaction}");
        }

        return EditLimitsValidationResult.Valid();
    }

}

/// <summary>
/// Result of query parameter validation
/// </summary>
public sealed record QueryValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public QueryParameters? ValidatedParameters { get; init; }

    public static QueryValidationResult Valid(QueryParameters parameters) =>
        new() { IsValid = true, ValidatedParameters = parameters };

    public static QueryValidationResult Invalid(string errorMessage) =>
        new() { IsValid = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Result of format validation
/// </summary>
public sealed record FormatValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ValidatedFormat { get; init; }

    public static FormatValidationResult Valid(string format) =>
        new() { IsValid = true, ValidatedFormat = format };

    public static FormatValidationResult Invalid(string errorMessage) =>
        new() { IsValid = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Result of SRID validation
/// </summary>
public sealed record SridValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ValidatedValue { get; init; }

    public static SridValidationResult Valid(string? value) =>
        new() { IsValid = true, ValidatedValue = value };

    public static SridValidationResult Invalid(string errorMessage) =>
        new() { IsValid = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Result of related records validation
/// </summary>
public sealed record RelatedRecordsValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public QueryRelatedRecordsParameters? ValidatedParameters { get; init; }

    public static RelatedRecordsValidationResult Valid(QueryRelatedRecordsParameters parameters) =>
        new() { IsValid = true, ValidatedParameters = parameters };

    public static RelatedRecordsValidationResult Invalid(string errorMessage) =>
        new() { IsValid = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Result of edit limits validation
/// </summary>
public sealed record EditLimitsValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorDetail { get; init; }

    public static EditLimitsValidationResult Valid() =>
        new() { IsValid = true };

    public static EditLimitsValidationResult Invalid(string errorMessage, string? errorDetail = null) =>
        new() { IsValid = false, ErrorMessage = errorMessage, ErrorDetail = errorDetail };
}
