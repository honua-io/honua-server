// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
namespace Honua.Core.Features.Validation.Abstractions;

/// <summary>
/// Common query parameter validation service used across protocols.
/// </summary>
public interface ICommonQueryValidator
{
    /// <summary>
    /// Validates standard pagination parameters across all protocols.
    /// </summary>
    /// <param name="offset">Offset/skip parameter</param>
    /// <param name="limit">Limit/take/count parameter</param>
    /// <returns>Validation result with either success or error details</returns>
    ValidationResult ValidatePagination(int? offset, int? limit);

    /// <summary>
    /// Validates and normalizes format parameter across protocols.
    /// </summary>
    /// <param name="format">Format parameter (json, geojson, xml, html, etc.)</param>
    /// <param name="allowedFormats">Set of allowed formats for this endpoint</param>
    /// <returns>Validation result with normalized format</returns>
    ValidationResult<string> ValidateFormat(string? format, IReadOnlySet<string> allowedFormats);

    /// <summary>
    /// Validates spatial reference system identifier.
    /// </summary>
    /// <param name="srid">SRID to validate</param>
    /// <param name="parameterName">Name of the parameter for error reporting</param>
    /// <returns>Validation result with normalized SRID</returns>
    ValidationResult<int?> ValidateSrid(string? srid, string parameterName);

    /// <summary>
    /// Validates query parameter names against an allowed list.
    /// </summary>
    /// <param name="queryParameterNames">Names of query parameters from the request</param>
    /// <param name="allowedParameters">Set of allowed parameter names</param>
    /// <returns>Validation result indicating success or parameter violations</returns>
    ValidationResult ValidateAllowedParameters(IReadOnlyCollection<string> queryParameterNames, IReadOnlySet<string> allowedParameters);

    /// <summary>
    /// Validates bounding box parameter format and values.
    /// </summary>
    /// <param name="bboxValue">Bounding box string (minx,miny,maxx,maxy)</param>
    /// <param name="targetSrid">Target SRID for coordinate validation</param>
    /// <returns>Validation result with parsed bounding box</returns>
    ValidationResult<BoundingBox> ValidateBbox(string? bboxValue, int targetSrid);

    /// <summary>
    /// Validates where clause for basic SQL injection patterns.
    /// </summary>
    /// <param name="whereClause">WHERE clause string</param>
    /// <returns>Validation result indicating security compliance</returns>
    ValidationResult ValidateWhereClause(string? whereClause);

    /// <summary>
    /// Validates and normalizes pagination parameters, returning effective values with defaults.
    /// </summary>
    /// <param name="offset">Offset/skip parameter (null uses 0)</param>
    /// <param name="limit">Limit/take/count parameter (null uses default from configuration)</param>
    /// <returns>Validation result with normalized offset and limit values</returns>
    ValidationResult<PaginationValues> ValidateAndNormalizePagination(int? offset, int? limit);

    /// <summary>
    /// Validates and normalizes pagination parameters with custom validation rules and parameter names.
    /// </summary>
    /// <param name="offset">Offset/skip parameter (null uses 0)</param>
    /// <param name="limit">Limit/take/count parameter (null uses default from configuration)</param>
    /// <param name="options">Validation options including minimums and parameter names.</param>
    /// <returns>Validation result with normalized offset and limit values</returns>
    ValidationResult<PaginationValues> ValidateAndNormalizePagination(
        int? offset,
        int? limit,
        PaginationValidationOptions options);

    /// <summary>
    /// Gets the configured query limits for use by protocols that need to expose limits.
    /// </summary>
    QueryLimits QueryLimits { get; }
}
