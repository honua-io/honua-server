// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Linq;
using Honua.Core.Configuration;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Validation;

public sealed class CommonQueryValidator : ICommonQueryValidator
{
    private readonly LimitsOptions _limitsOptions;

    public CommonQueryValidator(IOptions<LimitsOptions> limitsOptions)
    {
        _limitsOptions = limitsOptions?.Value ?? throw new ArgumentNullException(nameof(limitsOptions));
    }

    public QueryLimits QueryLimits => _limitsOptions.Query;

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

    public ValidationResult ValidateAllowedParameters(IReadOnlyCollection<string> queryParameterNames, IReadOnlySet<string> allowedParameters)
    {
        var unknownParameter = queryParameterNames.FirstOrDefault(parameterName =>
            !allowedParameters.Contains(parameterName) &&
            !ContainsIgnoreCase(allowedParameters, parameterName));

        return unknownParameter is null
            ? ValidationResult.Success()
            : ValidationResult.Failure($"Unknown query parameter: {unknownParameter}");
    }

    public ValidationResult<BoundingBox> ValidateBbox(string? bboxValue, int targetSrid)
    {
        if (string.IsNullOrWhiteSpace(bboxValue))
        {
            return ValidationResult<BoundingBox>.Success(null);
        }

        var tokens = bboxValue.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Length is not (4 or 6))
        {
            return ValidationResult<BoundingBox>.Failure(
                "Bounding box must contain exactly 4 comma-separated values: minx,miny,maxx,maxy");
        }

        var parsed = BoundingBoxOrdinates.Parse(tokens);
        if (!parsed.Succeeded)
        {
            return ValidationResult<BoundingBox>.Failure(
                "Bounding box coordinates must be valid numbers");
        }

        var coords = parsed.Ordinates;

        if (coords.Length == 6)
        {
            return ValidationResult<BoundingBox>.Failure("3D bounding boxes are not supported.");
        }

        var isGeographic = SpatialReference.Create(targetSrid).IsGeographic;
        if (coords[1] > coords[3] || (!isGeographic && coords[0] > coords[2]))
        {
            return ValidationResult<BoundingBox>.Failure(
                "Bounding box minimum coordinates must be less than maximum coordinates");
        }

        if (isGeographic &&
            (coords[0] < -180 || coords[0] > 180 || coords[2] < -180 || coords[2] > 180 ||
             coords[1] < -90 || coords[1] > 90 || coords[3] < -90 || coords[3] > 90))
        {
            return ValidationResult<BoundingBox>.Failure(
                "Geographic coordinates must be within valid ranges (longitude: -180 to 180, latitude: -90 to 90)");
        }

        var bbox = new BoundingBox(coords[0], coords[1], coords[2], coords[3]);
        return ValidationResult<BoundingBox>.Success(bbox);
    }

    // A successful denylist result is not proof the clause is injection-safe.
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

        // Mask the content of balanced single-quoted string literals before the
        // dangerous-pattern scan. Literal content is bound as a parameter, so "--",
        // "/* */", ";" or UNION inside a quoted literal (e.g. name='note -- x' or a
        // LIKE pattern '%/*%') is harmless data and must not be rejected. Real
        // injection breaks out of the quoting and survives masking (bug hunt).
        var inspectable = System.Text.RegularExpressions.Regex.Replace(
            whereClause, @"'(?:[^']|'')*'", "''");

        var hasDangerousPattern = dangerousPatterns.Any(pattern =>
            System.Text.RegularExpressions.Regex.IsMatch(inspectable, pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));

        return hasDangerousPattern
            ? ValidationResult.Failure("WHERE clause contains potentially dangerous SQL patterns")
            : ValidationResult.Success();
    }

    public ValidationResult<PaginationValues> ValidateAndNormalizePagination(int? offset, int? limit)
        => ValidateAndNormalizePagination(offset, limit, PaginationValidationOptions.Default);

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
            if (!options.ClampLimitToMaximum)
            {
                return ValidationResult<PaginationValues>.Failure(
                    $"{limitName} cannot exceed {_limitsOptions.Query.MaxRecordCount}. " +
                    $"Maximum record count: {_limitsOptions.Query.MaxRecordCount}.");
            }

            // OGC API - Features Part 1 /req/core/fc-limit-response-1: an over-maximum limit
            // SHALL NOT result in an error; the maximum is used as the parameter value instead.
            limit = _limitsOptions.Query.MaxRecordCount;
        }

        var effectiveLimit = limit ?? _limitsOptions.Query.DefaultRecordCount;

        if (effectiveLimit < minLimit)
        {
            return ValidationResult<PaginationValues>.Failure($"{limitName} must be {minLimit} or greater.");
        }

        return ValidationResult<PaginationValues>.Success(new PaginationValues(effectiveOffset, effectiveLimit));
    }

    private static bool ContainsIgnoreCase(IReadOnlySet<string> values, string value)
        => values.Any(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));
}

public sealed record PaginationValues(int Offset, int Limit);

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

public sealed record BoundingBox(double MinX, double MinY, double MaxX, double MaxY);
