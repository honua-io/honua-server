// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

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
    /// V2 overload of <see cref="Parse"/> + <see cref="Normalize(FilterExpression, MetadataV2Resource)"/>.
    /// Returns the normalized expression with field types resolved from
    /// <see cref="MetadataV2Resource.SchemaFields"/>. Call
    /// <see cref="Translate(FilterExpression?, MetadataV2Resource)"/> when the
    /// normalized expression also needs a provider SQL fragment.
    /// </summary>
    FilterParseResult ParseAndNormalize(FilterLanguage language, string? filter, MetadataV2Resource resource);

    /// <summary>
    /// Normalises an expression. Resolves field types from the resource's
    /// <see cref="MetadataV2Resource.SchemaFields"/> and returns the normalised
    /// expression.
    /// </summary>
    FilterExpression Normalize(FilterExpression expression, MetadataV2Resource resource);

    /// <summary>
    /// Normalises an expression and produces a SQL fragment using
    /// the resource's schema fields and spatial reference. Returns success with no
    /// SQL filter when <paramref name="expression"/> is null.
    /// </summary>
    /// <param name="expression">Filter expression to translate.</param>
    /// <param name="resource">Metadata v2 resource for validation and coercion.</param>
    /// <returns>Translation result with SQL fragment or error.</returns>
    FilterTranslationResult Translate(FilterExpression? expression, MetadataV2Resource resource);
}
