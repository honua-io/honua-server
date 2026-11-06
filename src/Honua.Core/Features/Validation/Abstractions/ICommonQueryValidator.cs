// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
namespace Honua.Core.Features.Validation.Abstractions;

public interface ICommonQueryValidator
{
    ValidationResult ValidatePagination(int? offset, int? limit);

    ValidationResult<string> ValidateFormat(string? format, IReadOnlySet<string> allowedFormats);

    ValidationResult<int?> ValidateSrid(string? srid, string parameterName);

    ValidationResult ValidateAllowedParameters(IReadOnlyCollection<string> queryParameterNames, IReadOnlySet<string> allowedParameters);

    ValidationResult<BoundingBox> ValidateBbox(string? bboxValue, int targetSrid);

    // This WHERE pre-filter is an incomplete denylist after masking quoted literals and is not the injection control; callers must still parse or bind the clause.
    ValidationResult ValidateWhereClause(string? whereClause);

    // A limit above the configured maximum fails validation instead of being clamped.
    ValidationResult<PaginationValues> ValidateAndNormalizePagination(int? offset, int? limit);

    ValidationResult<PaginationValues> ValidateAndNormalizePagination(
        int? offset,
        int? limit,
        PaginationValidationOptions options);

    QueryLimits QueryLimits { get; }
}
