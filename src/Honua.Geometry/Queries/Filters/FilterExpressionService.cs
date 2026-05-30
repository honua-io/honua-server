// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters.Cql2;
using Honua.Core.Queries.Filters.GeoServicesSql;
using Honua.Core.Queries.Filters.OData;

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Default implementation for shared filter parsing and translation.
/// </summary>
public sealed class FilterExpressionService : IFilterExpressionService
{
    private readonly IFilterExpressionTranslator _translator;

    /// <summary>
    /// Creates a new filter expression service.
    /// </summary>
    /// <param name="translator">Translator for converting expressions to SQL.</param>
    public FilterExpressionService(IFilterExpressionTranslator translator)
    {
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));
    }

    /// <inheritdoc />
    public FilterParseResult Parse(FilterLanguage language, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return FilterParseResult.Success(null);
        }

        try
        {
            FilterExpression expression = language switch
            {
                FilterLanguage.OData => new ODataFilterParser().Parse(filter),
                FilterLanguage.ArcGisSql => new GeoServicesSqlParser().Parse(filter),
                FilterLanguage.Cql2Text => new Cql2Parser().Parse(filter),
                FilterLanguage.Cql2Json => new Cql2JsonParser().Parse(filter),
                _ => throw new NotSupportedException($"Unsupported filter language '{language}'.")
            };

            return FilterParseResult.Success(expression);
        }
        catch (ArgumentException ex)
        {
            return FilterParseResult.Failure(ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return FilterParseResult.Failure(ex.Message);
        }
    }

    /// <inheritdoc />
    public FilterParseResult ParseAndNormalize(FilterLanguage language, string? filter, MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var parseResult = Parse(language, filter);
        if (!parseResult.IsSuccess || parseResult.Expression is null)
        {
            return parseResult;
        }
        try
        {
            var normalized = _translator.Normalize(parseResult.Expression, resource);
            return FilterParseResult.Success(normalized);
        }
        catch (ArgumentException ex)
        {
            return FilterParseResult.Failure(ex.Message);
        }
    }

    /// <inheritdoc />
    public FilterExpression Normalize(FilterExpression expression, MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return _translator.Normalize(expression, resource);
    }

    /// <inheritdoc />
    public FilterTranslationResult Translate(FilterExpression? expression, MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (expression == null)
        {
            return FilterTranslationResult.Success(null, null);
        }

        try
        {
            var sqlFilter = _translator.Translate(expression, resource);
            return FilterTranslationResult.Success(expression, sqlFilter);
        }
        catch (ArgumentException ex)
        {
            return FilterTranslationResult.Failure(ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return FilterTranslationResult.Failure(ex.Message);
        }
    }
}
