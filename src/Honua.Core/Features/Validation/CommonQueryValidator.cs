// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Validation;

/// <summary>
/// Implementation of common query validation patterns.
/// </summary>
public sealed class CommonQueryValidator : ICommonQueryValidator
{
    private readonly LimitsOptions _limitsOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommonQueryValidator"/> class.
    /// </summary>
    /// <param name="limitsOptions">Limits configuration</param>
    public CommonQueryValidator(IOptions<LimitsOptions> limitsOptions)
    {
        _limitsOptions = limitsOptions?.Value ?? throw new ArgumentNullException(nameof(limitsOptions));
    }

    /// <inheritdoc/>
    public QueryLimits QueryLimits => _limitsOptions.Query;

    /// <inheritdoc/>
    public ValidationResult ValidatePagination(int? offset, int? limit)
    {
        if (offset.HasValue)
        {
            if (offset.Value < 0)
            {
                return ValidationResult.Failure("Offset cannot be negative");
            }

            if (offset.Value > _limitsOptions.Query.MaxOffset)
            {
                return ValidationResult.Failure(
                    $"Offset cannot exceed {_limitsOptions.Query.MaxOffset}. " +
                    $"Maximum offset: {_limitsOptions.Query.MaxOffset}");
            }
        }

        if (limit.HasValue)
        {
            if (limit.Value < 0)
            {
                return ValidationResult.Failure("Limit cannot be negative");
            }

            if (limit.Value > _limitsOptions.Query.MaxRecordCount)
            {
                return ValidationResult.Failure(
                    $"Limit cannot exceed {_limitsOptions.Query.MaxRecordCount}. " +
                    $"Maximum record count: {_limitsOptions.Query.MaxRecordCount}");
            }
        }

        return ValidationResult.Success();
    }

    /// <inheritdoc/>
    public ValidationResult<string> ValidateFormat(string? format, IReadOnlySet<string> allowedFormats)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            var defaultFormat = allowedFormats.FirstOrDefault() ?? "json";
            return ValidationResult<string>.Success(defaultFormat);
        }

        var normalizedFormat = format.Trim().ToLowerInvariant();

        if (!allowedFormats.Contains(normalizedFormat) &&
            !ContainsIgnoreCase(allowedFormats, normalizedFormat))
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

        if (!int.TryParse(srid, out var sridValue))
        {
            return ValidationResult<int?>.Failure($"{parameterName} must be a valid integer");
        }

        if (sridValue < 0 || sridValue > 999999)
        {
            return ValidationResult<int?>.Failure(ErrorMessages.RangeValidation.SridRange);
        }

        return ValidationResult<int?>.Success(sridValue);
    }

    /// <inheritdoc/>
    public ValidationResult ValidateAllowedParameters(IReadOnlyCollection<string> queryParameterNames, IReadOnlySet<string> allowedParameters)
    {
        foreach (var parameterName in queryParameterNames)
        {
            if (!allowedParameters.Contains(parameterName) &&
                !ContainsIgnoreCase(allowedParameters, parameterName))
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

        ReadOnlySpan<char> bboxSpan = bboxValue.AsSpan();
        Span<double> coords = stackalloc double[6];
        var count = 0;
        foreach (var range in bboxSpan.Split(','))
        {
            var token = bboxSpan[range].Trim();
            if (token.IsEmpty)
            {
                continue;
            }

            if (count >= coords.Length)
            {
                return ValidationResult<BoundingBox>.Failure(
                    "Bounding box must contain exactly 4 comma-separated values: minx,miny,maxx,maxy");
            }

            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out coords[count]))
            {
                return ValidationResult<BoundingBox>.Failure(
                    "Bounding box coordinates must be valid numbers");
            }

            count++;
        }

        if (count == 6)
        {
            return ValidationResult<BoundingBox>.Failure("3D bounding boxes are not supported.");
        }

        if (count != 4)
        {
            return ValidationResult<BoundingBox>.Failure(
                "Bounding box must contain exactly 4 comma-separated values: minx,miny,maxx,maxy");
        }

        var isGeographic = SpatialReference.Create(targetSrid).IsGeographic;
        if (coords[1] > coords[3] || (!isGeographic && coords[0] > coords[2]))
        {
            return ValidationResult<BoundingBox>.Failure(
                "Bounding box minimum coordinates must be less than maximum coordinates");
        }

        if (isGeographic)
        {
            if (coords[0] < -180 || coords[0] > 180 || coords[2] < -180 || coords[2] > 180 ||
                coords[1] < -90 || coords[1] > 90 || coords[3] < -90 || coords[3] > 90)
            {
                return ValidationResult<BoundingBox>.Failure(
                    "Geographic coordinates must be within valid ranges (longitude: -180 to 180, latitude: -90 to 90)");
            }
        }

        var bbox = new BoundingBox(coords[0], coords[1], coords[2], coords[3]);
        return ValidationResult<BoundingBox>.Success(bbox);
    }

    /// <inheritdoc/>
    public ValidationResult ValidateWhereClause(string? whereClause)
    {
        if (string.IsNullOrWhiteSpace(whereClause))
        {
            return ValidationResult.Success();
        }

        for (var i = 0; i < whereClause.Length; i++)
        {
            if (char.IsControl(whereClause[i]))
            {
                return ValidationResult.Failure("WHERE clause contains invalid control characters");
            }
        }

        if (whereClause.Length > 4000)
        {
            return ValidationResult.Failure("WHERE clause is too long (maximum 4000 characters)");
        }

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

    /// <inheritdoc/>
    public ValidationResult<PaginationValues> ValidateAndNormalizePagination(int? offset, int? limit)
        => ValidateAndNormalizePagination(offset, limit, PaginationValidationOptions.Default);

    /// <inheritdoc/>
    public ValidationResult<PaginationValues> ValidateAndNormalizePagination(
        int? offset,
        int? limit,
        PaginationValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var minOffset = options.MinOffset;
        var minLimit = options.MinLimit;
        var offsetName = string.IsNullOrWhiteSpace(options.OffsetParameterName) ? "Offset" : options.OffsetParameterName;
        var limitName = string.IsNullOrWhiteSpace(options.LimitParameterName) ? "Limit" : options.LimitParameterName;

        if (offset.HasValue && offset.Value < minOffset)
        {
            return ValidationResult<PaginationValues>.Failure($"{offsetName} must be {minOffset} or greater.");
        }

        if (offset.HasValue && offset.Value > _limitsOptions.Query.MaxOffset)
        {
            return ValidationResult<PaginationValues>.Failure(
                $"{offsetName} cannot exceed {_limitsOptions.Query.MaxOffset}. " +
                $"Maximum offset: {_limitsOptions.Query.MaxOffset}.");
        }

        var effectiveOffset = offset ?? minOffset;

        if (limit.HasValue && limit.Value < minLimit)
        {
            return ValidationResult<PaginationValues>.Failure($"{limitName} must be {minLimit} or greater.");
        }

        if (limit.HasValue && limit.Value > _limitsOptions.Query.MaxRecordCount)
        {
            return ValidationResult<PaginationValues>.Failure(
                $"{limitName} cannot exceed {_limitsOptions.Query.MaxRecordCount}. " +
                $"Maximum record count: {_limitsOptions.Query.MaxRecordCount}.");
        }

        var effectiveLimit = limit ?? _limitsOptions.Query.DefaultRecordCount;

        if (effectiveLimit < minLimit)
        {
            return ValidationResult<PaginationValues>.Failure($"{limitName} must be {minLimit} or greater.");
        }

        return ValidationResult<PaginationValues>.Success(new PaginationValues(effectiveOffset, effectiveLimit));
    }

    private static bool ContainsIgnoreCase(IReadOnlySet<string> values, string value)
    {
        foreach (var candidate in values)
        {
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Normalized pagination values with applied defaults and limits.
/// </summary>
/// <param name="Offset">The effective offset value (0 if not specified).</param>
/// <param name="Limit">The effective limit value (default from configuration if not specified).</param>
public sealed record PaginationValues(int Offset, int Limit);

/// <summary>
/// Generic validation result that can carry a typed value.
/// </summary>
/// <typeparam name="T">Type of the validation result value</typeparam>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
    Justification = "Factory methods keep validation call sites concise.")]
public sealed class ValidationResult<T>
{
    /// <summary>
    /// Whether the validation succeeded.
    /// </summary>
    public bool IsValid { get; }

    /// <summary>
    /// The validated value when successful.
    /// </summary>
    public T? Value { get; }

    /// <summary>
    /// Error message when validation fails.
    /// </summary>
    public string? ErrorMessage { get; }

    private ValidationResult(bool isValid, T? value, string? errorMessage)
    {
        IsValid = isValid;
        Value = value;
        ErrorMessage = errorMessage;
    }

    /// <summary>
    /// Creates a success result.
    /// </summary>
    public static ValidationResult<T> Success(T? value) => new(true, value, null);

    /// <summary>
    /// Creates a failure result.
    /// </summary>
    public static ValidationResult<T> Failure(string errorMessage) => new(false, default, errorMessage);
}

/// <summary>
/// Simple validation result without a typed value.
/// </summary>
public sealed class ValidationResult
{
    /// <summary>
    /// Whether the validation succeeded.
    /// </summary>
    public bool IsValid { get; }

    /// <summary>
    /// Error message when validation fails.
    /// </summary>
    public string? ErrorMessage { get; }

    private ValidationResult(bool isValid, string? errorMessage)
    {
        IsValid = isValid;
        ErrorMessage = errorMessage;
    }

    /// <summary>
    /// Creates a success result.
    /// </summary>
    public static ValidationResult Success() => new(true, null);

    /// <summary>
    /// Creates a failure result.
    /// </summary>
    public static ValidationResult Failure(string errorMessage) => new(false, errorMessage);
}

/// <summary>
/// Simple bounding box structure for validation results.
/// </summary>
/// <param name="MinX">Minimum X coordinate</param>
/// <param name="MinY">Minimum Y coordinate</param>
/// <param name="MaxX">Maximum X coordinate</param>
/// <param name="MaxY">Maximum Y coordinate</param>
public sealed record BoundingBox(double MinX, double MinY, double MaxX, double MaxY);
