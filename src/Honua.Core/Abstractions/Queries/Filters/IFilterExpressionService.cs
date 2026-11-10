// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Queries.Filters;

public enum FilterLanguage
{
    OData,

    GeoServicesSql,

    Cql2Text,

    Cql2Json
}

public sealed record FilterParseResult
{
    public bool IsSuccess { get; init; }

    public FilterExpression? Expression { get; init; }

    public string? ErrorMessage { get; init; }

    public static FilterParseResult Success(FilterExpression? expression) =>
        new() { IsSuccess = true, Expression = expression };

    public static FilterParseResult Failure(string errorMessage) =>
        new() { IsSuccess = false, ErrorMessage = errorMessage };
}

public sealed record FilterTranslationResult
{
    public bool IsSuccess { get; init; }

    public FilterExpression? Expression { get; init; }

    public SqlFragment? SqlFilter { get; init; }

    public string? ErrorMessage { get; init; }

    // An unknown field is a skip for one resource, not a rejection of the whole filter.
    public bool IsUnknownField { get; init; }

    public static FilterTranslationResult Success(FilterExpression? expression, SqlFragment? sqlFilter) =>
        new() { IsSuccess = true, Expression = expression, SqlFilter = sqlFilter };

    public static FilterTranslationResult Failure(string errorMessage, bool isUnknownField = false) =>
        new() { IsSuccess = false, ErrorMessage = errorMessage, IsUnknownField = isUnknownField };
}

public interface IFilterExpressionService
{
    FilterParseResult Parse(FilterLanguage language, string? filter);

    FilterParseResult ParseAndNormalize(FilterLanguage language, string? filter, MetadataV2Resource resource);

    FilterExpression Normalize(FilterExpression expression, MetadataV2Resource resource);

    FilterTranslationResult Translate(FilterExpression? expression, MetadataV2Resource resource);
}
