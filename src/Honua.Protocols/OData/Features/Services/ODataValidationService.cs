// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.OData.Services;

internal sealed class ODataValidationService
{
    private readonly ICommonQueryValidator _commonQueryValidator;
    private readonly int _maxPageSize;
    private static readonly PaginationValidationOptions _odataPagination =
        // OData v4 allows $top=0 (a deliberately empty page, commonly paired with
        // $count=true to retrieve only the total). MinLimit: 0 lets $top=0 validate
        // instead of returning a spec-violating 400; negative $top is still rejected.
        new(MinOffset: 0, MinLimit: 0, OffsetParameterName: "$skip", LimitParameterName: "$top");

    public ODataValidationService(
        ICommonQueryValidator commonQueryValidator,
        IOptions<ODataOptions> odataOptions)
    {
        _commonQueryValidator = commonQueryValidator;
        ArgumentNullException.ThrowIfNull(odataOptions);
        _maxPageSize = odataOptions.Value.MaxPageSize;
    }

    public ValidationResult ValidateAllowedParameters(IReadOnlyCollection<string> queryParameterNames, IReadOnlySet<string> allowedParameters)
    {
        return _commonQueryValidator.ValidateAllowedParameters(queryParameterNames, allowedParameters);
    }

    public ValidationResult ValidatePagination(int? offset, int? limit)
    {
        return _commonQueryValidator.ValidatePagination(offset, limit);
    }

    public ValidationResult<string> ValidateFormat(string? format, IReadOnlySet<string> allowedFormats)
    {
        return _commonQueryValidator.ValidateFormat(format, allowedFormats);
    }

    public ValidationResult<int?> ValidateSrid(string? srid, string parameterName)
    {
        return _commonQueryValidator.ValidateSrid(srid, parameterName);
    }

    public ValidationResult<BoundingBox> ValidateBbox(string? bboxValue, int targetSrid)
    {
        return _commonQueryValidator.ValidateBbox(bboxValue, targetSrid);
    }

    public ValidationResult ValidateWhereClause(string? whereClause)
    {
        return _commonQueryValidator.ValidateWhereClause(whereClause);
    }

    public int MaxPageSize => _maxPageSize;

    public int ClampPageSize(int limit) => limit switch
    {
        0 => 0,
        < 0 => _maxPageSize,
        _ => Math.Min(limit, _maxPageSize),
    };

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

    public QueryLimits QueryLimits => _commonQueryValidator.QueryLimits;
}
