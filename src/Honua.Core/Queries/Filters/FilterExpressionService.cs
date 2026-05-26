// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters.Cql2;
using Honua.Core.Queries.Filters.GeoServicesSql;
using Honua.Core.Queries.Filters.OData;

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Supported filter languages for shared parsing and translation.
/// </summary>
public enum FilterLanguage
{
    /// <summary>OData $filter expressions.</summary>
    OData,

    /// <summary>GeoServices SQL (FeatureServer where clauses).</summary>
    ArcGisSql,

    /// <summary>CQL2 text expressions.</summary>
    Cql2Text,

    /// <summary>CQL2 JSON expressions.</summary>
    Cql2Json
}

/// <summary>
/// Result of parsing a filter expression string.
/// </summary>
public sealed record FilterParseResult
{
    /// <summary>
    /// Indicates whether parsing succeeded.
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// Parsed filter expression, if successful.
    /// </summary>
    public FilterExpression? Expression { get; init; }

    /// <summary>
    /// Error message when parsing fails.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a successful parse result.
    /// </summary>
    /// <param name="expression">Parsed filter expression.</param>
    public static FilterParseResult Success(FilterExpression? expression) =>
        new() { IsSuccess = true, Expression = expression };

    /// <summary>
    /// Creates a failed parse result.
    /// </summary>
    /// <param name="errorMessage">Failure reason.</param>
    public static FilterParseResult Failure(string errorMessage) =>
        new() { IsSuccess = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Result of translating a filter expression to SQL.
/// </summary>
public sealed record FilterTranslationResult
{
    /// <summary>
    /// Indicates whether translation succeeded.
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// Filter expression that was translated.
    /// </summary>
    public FilterExpression? Expression { get; init; }

    /// <summary>
    /// SQL fragment produced from the filter expression.
    /// </summary>
    public SqlFragment? SqlFilter { get; init; }

    /// <summary>
    /// Error message when translation fails.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a successful translation result.
    /// </summary>
    /// <param name="expression">Translated expression.</param>
    /// <param name="sqlFilter">Translated SQL fragment.</param>
    public static FilterTranslationResult Success(FilterExpression? expression, SqlFragment? sqlFilter) =>
        new() { IsSuccess = true, Expression = expression, SqlFilter = sqlFilter };

    /// <summary>
    /// Creates a failed translation result.
    /// </summary>
    /// <param name="errorMessage">Failure reason.</param>
    public static FilterTranslationResult Failure(string errorMessage) =>
        new() { IsSuccess = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Shared parser and translator for filter expressions across protocols.
/// </summary>
public interface IFilterExpressionService
{
    /// <summary>
    /// Parses a filter string into a filter expression.
    /// </summary>
    /// <param name="language">Filter language.</param>
    /// <param name="filter">Filter string.</param>
    /// <returns>Parse result with expression or error.</returns>
    FilterParseResult Parse(FilterLanguage language, string? filter);

    /// <summary>
    /// Parses and translates a filter string into a SQL fragment.
    /// </summary>
    /// <param name="language">Filter language.</param>
    /// <param name="filter">Filter string.</param>
    /// <param name="layer">Layer definition for validation and coercion.</param>
    /// <returns>Translation result with SQL fragment or error.</returns>
    FilterTranslationResult Translate(FilterLanguage language, string? filter, LayerDefinition layer);

    /// <summary>
    /// Translates a filter expression into a SQL fragment.
    /// </summary>
    /// <param name="expression">Filter expression (null yields success with no SQL filter).</param>
    /// <param name="layer">Layer definition for validation and coercion.</param>
    /// <returns>Translation result with SQL fragment or error.</returns>
    FilterTranslationResult Translate(FilterExpression? expression, LayerDefinition layer);

    /// <summary>
    /// V2 overload of <see cref="Parse"/> + <see cref="Normalize(FilterExpression, MetadataV2Resource)"/>.
    /// Returns the normalized expression with field types resolved from
    /// <see cref="MetadataV2Resource.SchemaFields"/>. Does NOT produce a SQL fragment —
    /// the provider-specific SQL backends still take <see cref="LayerDefinition"/>;
    /// callers that need a <see cref="SqlFragment"/> route via the v1 overload.
    /// </summary>
    FilterParseResult ParseAndNormalize(FilterLanguage language, string? filter, MetadataV2Resource resource);

    /// <summary>
    /// V2 overload of normalisation. Resolves field types from the resource's
    /// <see cref="MetadataV2Resource.SchemaFields"/> and returns the normalised
    /// expression.
    /// </summary>
    FilterExpression Normalize(FilterExpression expression, MetadataV2Resource resource);

    /// <summary>
    /// V2 overload that normalises an expression and produces a SQL fragment using
    /// the resource's schema fields and spatial reference. Returns success with no
    /// SQL filter when <paramref name="expression"/> is null.
    /// </summary>
    /// <param name="expression">Filter expression to translate.</param>
    /// <param name="resource">Metadata v2 resource for validation and coercion.</param>
    /// <returns>Translation result with SQL fragment or error.</returns>
    FilterTranslationResult Translate(FilterExpression? expression, MetadataV2Resource resource);
}

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
    public FilterTranslationResult Translate(FilterLanguage language, string? filter, LayerDefinition layer)
    {
        var parseResult = Parse(language, filter);
        if (!parseResult.IsSuccess)
        {
            return FilterTranslationResult.Failure(parseResult.ErrorMessage ?? "Invalid filter expression.");
        }

        return Translate(parseResult.Expression, layer);
    }

    /// <inheritdoc />
    public FilterTranslationResult Translate(FilterExpression? expression, LayerDefinition layer)
    {
        if (expression == null)
        {
            return FilterTranslationResult.Success(null, null);
        }

        try
        {
            var sqlFilter = _translator.Translate(expression, layer);
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
