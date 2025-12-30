// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Validation;

/// <summary>
/// Common query parameter validation service that consolidates validation patterns
/// used across all protocols (GeoServices REST, OGC API Features, OData, MVT).
/// Provides centralized parameter validation to reduce duplication.
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
    ValidationResult<string> ValidateFormat(string? format, ISet<string> allowedFormats);

    /// <summary>
    /// Validates spatial reference system identifier.
    /// </summary>
    /// <param name="srid">SRID to validate</param>
    /// <param name="parameterName">Name of the parameter for error reporting</param>
    /// <returns>Validation result with normalized SRID</returns>
    ValidationResult<int?> ValidateSrid(string? srid, string parameterName);

    /// <summary>
    /// Validates query parameters against allowed parameter list.
    /// Common pattern used across OGC API Features, OData, and other protocols.
    /// </summary>
    /// <param name="queryParameters">Query parameters from HTTP request</param>
    /// <param name="allowedParameters">Set of allowed parameter names</param>
    /// <returns>Validation result indicating success or parameter violations</returns>
    ValidationResult ValidateAllowedParameters(IQueryCollection queryParameters, ISet<string> allowedParameters);

    /// <summary>
    /// Validates bounding box parameter format and values.
    /// </summary>
    /// <param name="bboxValue">Bounding box string (minx,miny,maxx,maxy)</param>
    /// <param name="targetSrid">Target SRID for coordinate validation</param>
    /// <returns>Validation result with parsed bounding box</returns>
    ValidationResult<BoundingBox> ValidateBbox(string? bboxValue, int targetSrid);

    /// <summary>
    /// Validates where clause for basic SQL injection patterns.
    /// Supplements the CQL2 parser with security validation.
    /// </summary>
    /// <param name="whereClause">WHERE clause string</param>
    /// <returns>Validation result indicating security compliance</returns>
    ValidationResult ValidateWhereClause(string? whereClause);
}

/// <summary>
/// Implementation of common query validation patterns.
/// </summary>
internal sealed class CommonQueryValidator : ICommonQueryValidator
{
    private readonly LimitsOptions _limitsOptions;

    public CommonQueryValidator(IOptions<LimitsOptions> limitsOptions)
    {
        _limitsOptions = limitsOptions?.Value ?? throw new ArgumentNullException(nameof(limitsOptions));
    }

    /// <inheritdoc/>
    public ValidationResult ValidatePagination(int? offset, int? limit)
    {
        // Validate offset
        if (offset.HasValue)
        {
            if (offset.Value < 0)
            {
                return ValidationResult.Failure("Offset cannot be negative");
            }

            if (offset.Value > _limitsOptions.Query.MaxOffset)
            {
                return ValidationResult.Failure($"Offset cannot exceed {_limitsOptions.Query.MaxOffset}");
            }
        }

        // Validate limit
        if (limit.HasValue)
        {
            if (limit.Value < 0)
            {
                return ValidationResult.Failure("Limit cannot be negative");
            }

            if (limit.Value > _limitsOptions.Query.MaxRecordCount)
            {
                return ValidationResult.Failure($"Limit cannot exceed {_limitsOptions.Query.MaxRecordCount}");
            }
        }

        return ValidationResult.Success();
    }

    /// <inheritdoc/>
    public ValidationResult<string> ValidateFormat(string? format, ISet<string> allowedFormats)
    {
        // Default format handling
        if (string.IsNullOrWhiteSpace(format))
        {
            // Return first format as default, typically "json"
            var defaultFormat = allowedFormats.FirstOrDefault() ?? "json";
            return ValidationResult<string>.Success(defaultFormat);
        }

        // Normalize to lowercase for comparison
        var normalizedFormat = format.Trim().ToLowerInvariant();

        // Check if format is allowed
        if (!allowedFormats.Any(f => string.Equals(f, normalizedFormat, StringComparison.OrdinalIgnoreCase)))
        {
            return ValidationResult<string>.Failure(
                $"Unsupported format '{format}'. Allowed formats: {string.Join(", ", allowedFormats)}");
        }

        return ValidationResult<string>.Success(normalizedFormat);
    }

    /// <inheritdoc/>
    public ValidationResult<int?> ValidateSrid(string? srid, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(srid))
        {
            return ValidationResult<int?>.Success(null);
        }

        // Try to parse SRID
        if (!int.TryParse(srid, out var sridValue))
        {
            return ValidationResult<int?>.Failure($"{parameterName} must be a valid integer");
        }

        // Validate SRID range (based on EPSG standards)
        if (sridValue < 0 || sridValue > 999999)
        {
            return ValidationResult<int?>.Failure($"{parameterName} must be between 0 and 999,999");
        }

        return ValidationResult<int?>.Success(sridValue);
    }

    /// <inheritdoc/>
    public ValidationResult ValidateAllowedParameters(IQueryCollection queryParameters, ISet<string> allowedParameters)
    {
        foreach (var parameterName in queryParameters.Keys)
        {
            if (!allowedParameters.Contains(parameterName, StringComparer.OrdinalIgnoreCase))
            {
                return ValidationResult.Failure($"Unknown query parameter: {parameterName}");
            }
        }

        return ValidationResult.Success();
    }

    /// <inheritdoc/>
    public ValidationResult<BoundingBox> ValidateBbox(string? bboxValue, int targetSrid)
    {
        if (string.IsNullOrWhiteSpace(bboxValue))
        {
            return ValidationResult<BoundingBox>.Success(null);
        }

        var parts = bboxValue.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            return ValidationResult<BoundingBox>.Failure(
                "Bounding box must contain exactly 4 comma-separated values: minx,miny,maxx,maxy");
        }

        if (!double.TryParse(parts[0], out var minX) ||
            !double.TryParse(parts[1], out var minY) ||
            !double.TryParse(parts[2], out var maxX) ||
            !double.TryParse(parts[3], out var maxY))
        {
            return ValidationResult<BoundingBox>.Failure(
                "Bounding box coordinates must be valid numbers");
        }

        // Validate coordinate order
        if (minX >= maxX || minY >= maxY)
        {
            return ValidationResult<BoundingBox>.Failure(
                "Bounding box minimum coordinates must be less than maximum coordinates");
        }

        // Basic coordinate validation for geographic systems (WGS84/pseudo-mercator)
        if (targetSrid == 4326)
        {
            if (minX < -180 || maxX > 180 || minY < -90 || maxY > 90)
            {
                return ValidationResult<BoundingBox>.Failure(
                    "Geographic coordinates must be within valid ranges (longitude: -180 to 180, latitude: -90 to 90)");
            }
        }

        var bbox = new BoundingBox(minX, minY, maxX, maxY);
        return ValidationResult<BoundingBox>.Success(bbox);
    }

    /// <inheritdoc/>
    public ValidationResult ValidateWhereClause(string? whereClause)
    {
        if (string.IsNullOrWhiteSpace(whereClause))
        {
            return ValidationResult.Success();
        }

        // Length validation
        if (whereClause.Length > 4000)
        {
            return ValidationResult.Failure("WHERE clause is too long (maximum 4000 characters)");
        }

        // Basic SQL injection pattern detection (supplements CQL2 parser validation)
        var dangerousPatterns = new[]
        {
            @";\s*(?:DROP|DELETE|UPDATE|INSERT|CREATE|ALTER|EXEC|EXECUTE|DECLARE|xp_|sp_)",
            @"--",
            @"/\*",
            @"\*/",
            @"\bUNION\b",
            @"\bOR\b\s+\d+\s*=\s*\d+"
        };

        foreach (var pattern in dangerousPatterns)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(whereClause, pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return ValidationResult.Failure("WHERE clause contains potentially dangerous SQL patterns");
            }
        }

        return ValidationResult.Success();
    }
}

/// <summary>
/// Generic validation result that can carry a typed value.
/// </summary>
/// <typeparam name="T">Type of the validation result value</typeparam>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
    Justification = "Factory methods keep validation call sites concise.")]
public sealed class ValidationResult<T>
{
    public bool IsValid { get; }
    public T? Value { get; }
    public string? ErrorMessage { get; }

    private ValidationResult(bool isValid, T? value, string? errorMessage)
    {
        IsValid = isValid;
        Value = value;
        ErrorMessage = errorMessage;
    }

    public static ValidationResult<T> Success(T? value) => new(true, value, null);
    public static ValidationResult<T> Failure(string errorMessage) => new(false, default, errorMessage);
}

/// <summary>
/// Simple validation result without a typed value.
/// </summary>
public sealed class ValidationResult
{
    public bool IsValid { get; }
    public string? ErrorMessage { get; }

    private ValidationResult(bool isValid, string? errorMessage)
    {
        IsValid = isValid;
        ErrorMessage = errorMessage;
    }

    public static ValidationResult Success() => new(true, null);
    public static ValidationResult Failure(string errorMessage) => new(false, errorMessage);
}

/// <summary>
/// Simple bounding box structure for validation results.
/// </summary>
public sealed record BoundingBox(double MinX, double MinY, double MaxX, double MaxY);
