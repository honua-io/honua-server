// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using Honua.Server.Features.Infrastructure.Validation;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Composite validation service for OData endpoints that combines both common and feature-specific validation.
/// Reduces handler dependencies by providing a single validation interface.
/// </summary>
internal sealed class ODataValidationService
{
    private readonly IFeatureQueryValidator _featureQueryValidator;
    private readonly ICommonQueryValidator _commonQueryValidator;

    /// <summary>
    /// Initializes a new instance of the <see cref="ODataValidationService"/> class.
    /// </summary>
    public ODataValidationService(
        IFeatureQueryValidator featureQueryValidator,
        ICommonQueryValidator commonQueryValidator)
    {
        _featureQueryValidator = featureQueryValidator;
        _commonQueryValidator = commonQueryValidator;
    }

    /// <summary>
    /// Validates and applies query limits to the provided parameters.
    /// </summary>
    public QueryValidationResult ValidateQueryLimits(QueryParameters queryParams)
    {
        return _featureQueryValidator.ValidateQueryLimits(queryParams);
    }

    /// <summary>
    /// Validates and applies related records query limits to the provided parameters.
    /// </summary>
    public RelatedRecordsValidationResult ValidateRelatedRecordsLimits(QueryRelatedRecordsParameters queryParams)
    {
        return _featureQueryValidator.ValidateRelatedRecordsLimits(queryParams);
    }

    /// <summary>
    /// Validates query parameters against allowed parameter list.
    /// </summary>
    public ValidationResult ValidateAllowedParameters(IQueryCollection queryParameters, IReadOnlySet<string> allowedParameters)
    {
        return _commonQueryValidator.ValidateAllowedParameters(queryParameters, allowedParameters);
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
        if (limit.HasValue && limit.Value <= 0)
        {
            return ValidationResult<PaginationValues>.Failure("$top must be a positive integer.");
        }

        if (offset.HasValue && offset.Value < 0)
        {
            return ValidationResult<PaginationValues>.Failure("$skip must be a non-negative integer.");
        }

        return _commonQueryValidator.ValidateAndNormalizePagination(offset, limit);
    }

    /// <summary>
    /// Gets the configured query limits for use by protocols that need to expose limits.
    /// </summary>
    public QueryLimits QueryLimits => _commonQueryValidator.QueryLimits;
}
