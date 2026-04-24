// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;

namespace Honua.Server.Features.Protocols.OData.Services;

/// <summary>
/// Composite validation service for OData endpoints that combines both common and feature-specific validation.
/// Reduces handler dependencies by providing a single validation interface.
/// </summary>
internal sealed class ODataValidationService
{
    private readonly ICommonQueryValidator _commonQueryValidator;
    private static readonly PaginationValidationOptions _odataPagination =
        new(MinOffset: 0, MinLimit: 1, OffsetParameterName: "$skip", LimitParameterName: "$top");

    /// <summary>
    /// Initializes a new instance of the <see cref="ODataValidationService"/> class.
    /// </summary>
    public ODataValidationService(ICommonQueryValidator commonQueryValidator)
    {
        _commonQueryValidator = commonQueryValidator;
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
    /// Validates and normalizes pagination parameters, returning effective values with defaults.
    /// </summary>
    public ValidationResult<PaginationValues> ValidateAndNormalizePagination(int? offset, int? limit)
    {
        return _commonQueryValidator.ValidateAndNormalizePagination(offset, limit, _odataPagination);
    }

    /// <summary>
    /// Gets the configured query limits for use by protocols that need to expose limits.
    /// </summary>
    public QueryLimits QueryLimits => _commonQueryValidator.QueryLimits;
}
