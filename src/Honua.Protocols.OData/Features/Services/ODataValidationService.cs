// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.OData.Services;

/// <summary>
/// Composite validation service for OData endpoints that combines both common and feature-specific validation.
/// Reduces handler dependencies by providing a single validation interface.
/// </summary>
internal sealed class ODataValidationService
{
    private readonly ICommonQueryValidator _commonQueryValidator;
    private readonly int _maxPageSize;
    private static readonly PaginationValidationOptions _odataPagination =
        // OData v4 allows $top=0 (a deliberately empty page, commonly paired with
        // $count=true to retrieve only the total). MinLimit: 0 lets $top=0 validate
        // instead of returning a spec-violating 400; negative $top is still rejected.
        new(MinOffset: 0, MinLimit: 0, OffsetParameterName: "$skip", LimitParameterName: "$top");

    /// <summary>
    /// Initializes a new instance of the <see cref="ODataValidationService"/> class.
    /// </summary>
    public ODataValidationService(
        ICommonQueryValidator commonQueryValidator,
        IOptions<ODataOptions> odataOptions)
    {
        _commonQueryValidator = commonQueryValidator;
        ArgumentNullException.ThrowIfNull(odataOptions);
        _maxPageSize = odataOptions.Value.MaxPageSize;
    }

    /// <summary>
    /// Validates query parameters against allowed parameter list.
    /// </summary>
    public ValidationResult ValidateAllowedParameters(IReadOnlyCollection<string> queryParameterNames, IReadOnlySet<string> allowedParameters)
    {
        return _commonQueryValidator.ValidateAllowedParameters(queryParameterNames, allowedParameters);
    }

    /// <summary>
    /// Validates standard pagination parameters across all protocols.
    /// </summary>
    public ValidationResult ValidatePagination(int? offset, int? limit)
    {
        return _commonQueryValidator.ValidatePagination(offset, limit);
    }

    /// <summary>
    /// Validates and normalizes format parameter across protocols.
    /// </summary>
    public ValidationResult<string> ValidateFormat(string? format, IReadOnlySet<string> allowedFormats)
    {
        return _commonQueryValidator.ValidateFormat(format, allowedFormats);
    }

    /// <summary>
    /// Validates spatial reference system identifier.
    /// </summary>
    public ValidationResult<int?> ValidateSrid(string? srid, string parameterName)
    {
        return _commonQueryValidator.ValidateSrid(srid, parameterName);
    }

    /// <summary>
    /// Validates bounding box parameter format and values.
    /// </summary>
    public ValidationResult<BoundingBox> ValidateBbox(string? bboxValue, int targetSrid)
    {
        return _commonQueryValidator.ValidateBbox(bboxValue, targetSrid);
    }

    /// <summary>
    /// Validates where clause for basic SQL injection patterns.
    /// </summary>
    public ValidationResult ValidateWhereClause(string? whereClause)
    {
        return _commonQueryValidator.ValidateWhereClause(whereClause);
    }

    /// <summary>
    /// The server-driven paging cap applied to OData <c>$top</c>. The effective
    /// page size never exceeds this value; larger requests page via
    /// <c>@odata.nextLink</c>.
    /// </summary>
    public int MaxPageSize => _maxPageSize;

    /// <summary>
    /// Clamps a requested page size to the configured server-driven paging cap.
    /// A negative limit yields the cap so callers always materialize a bounded
    /// page; an explicit limit of <c>0</c> (OData <c>$top=0</c>) is preserved so
    /// the caller returns an empty page rather than a full server page.
    /// </summary>
    public int ClampPageSize(int limit) => limit switch
    {
        0 => 0,
        < 0 => _maxPageSize,
        _ => Math.Min(limit, _maxPageSize),
    };

    /// <summary>
    /// Validates and normalizes pagination parameters, returning effective values with defaults.
    /// The effective limit is clamped to <see cref="MaxPageSize"/> so a single OData request
    /// never materializes an unbounded page; the streaming handler emits an <c>@odata.nextLink</c>
    /// carrying the clamped <c>$top</c> so clients page through the remaining rows.
    /// </summary>
    public ValidationResult<PaginationValues> ValidateAndNormalizePagination(int? offset, int? limit)
    {
        var result = _commonQueryValidator.ValidateAndNormalizePagination(offset, limit, _odataPagination);
        if (!result.IsValid || result.Value is null)
        {
            return result;
        }

        var normalized = result.Value;
        var clampedLimit = ClampPageSize(normalized.Limit);
        return clampedLimit == normalized.Limit
            ? result
            : ValidationResult<PaginationValues>.Success(normalized with { Limit = clampedLimit });
    }

    /// <summary>
    /// Gets the configured query limits for use by protocols that need to expose limits.
    /// </summary>
    public QueryLimits QueryLimits => _commonQueryValidator.QueryLimits;
}
