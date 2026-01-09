// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Log category for OData query operations.
/// </summary>
internal sealed class ODataQueryLog;

/// <summary>
/// Service for handling OData query operations including filtering, ordering, pagination, and field selection.
/// Converts OData query parameters to SQL fragments and handles query result processing.
/// </summary>
internal sealed partial class ODataQueryService
{
    private readonly IFilterExpressionService _filterExpressionService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ODataQueryService"/> class.
    /// </summary>
    public ODataQueryService(IFilterExpressionService filterExpressionService)
    {
        _filterExpressionService = filterExpressionService ?? throw new ArgumentNullException(nameof(filterExpressionService));
    }

    /// <summary>
    /// Builds a feature query from OData parameters with proper validation and conversion.
    /// </summary>
    public FeatureQuery BuildFeatureQuery(
        string? filter,
        string? orderby,
        int? resultRecordCount,
        int? resultOffset,
        LayerDefinition layer,
        out string? error)
    {
        error = null;

        SqlFragment? sqlFilter = null;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            try
            {
                sqlFilter = ConvertODataFilterToSqlFragment(filter, layer);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                error = ex.Message;
                return new FeatureQuery();
            }
        }

        return new FeatureQuery
        {
            Where = null,
            SqlFilter = sqlFilter,
            SpatialFilter = null,
            SpatialReferenceSrid = layer.SpatialReference.ToSrid(),
            OrderBy = ParseODataOrderBy(orderby, layer),
            Limit = resultRecordCount,
            Offset = resultOffset
        };
    }

    /// <summary>
    /// Applies basic filtering to layer collections using simple OData expressions.
    /// </summary>
    public IEnumerable<LayerDefinition> ApplyBasicFilter(
        IEnumerable<LayerDefinition> layers,
        string filter)
    {
        // Simple name filtering - production would use a proper OData expression parser
        if (filter.Contains("name", StringComparison.OrdinalIgnoreCase))
        {
            var nameMatch = LayerNameFilterRegex().Match(filter);
            if (nameMatch.Success)
            {
                var nameValue = nameMatch.Groups[1].Value;
                return layers.Where(l => string.Equals(l.Name, nameValue, StringComparison.OrdinalIgnoreCase));
            }
        }

        return layers;
    }

    /// <summary>
    /// Applies field selection to result objects using an AOT-compatible approach.
    /// </summary>
    public object[] ApplyFieldSelection(Dictionary<string, object?>[] data, string select)
    {
        var fields = select.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return data.Select(item =>
        {
            var dict = new Dictionary<string, object?>();

            if (item is IDictionary<string, object?> existingDict)
            {
                // If it's already a dictionary, filter based on selected fields
                foreach (var kvp in existingDict)
                {
                    if (fields.Contains(kvp.Key))
                    {
                        dict[kvp.Key] = kvp.Value;
                    }
                }
            }

            return dict;
        }).ToArray();
    }

    /// <summary>
    /// Converts an OData $filter expression into a parameterized SQL fragment.
    /// </summary>
    public SqlFragment? ConvertODataFilterToSqlFragment(string? odataFilter, LayerDefinition layer)
    {
        if (string.IsNullOrWhiteSpace(odataFilter))
        {
            return null;
        }

        var translationResult = _filterExpressionService.Translate(FilterLanguage.OData, odataFilter, layer);
        if (!translationResult.IsSuccess)
        {
            throw new ArgumentException(translationResult.ErrorMessage ?? "Invalid OData filter.");
        }

        return translationResult.SqlFilter;
    }

    /// <summary>
    /// Parses OData $orderby expression into OrderByClause array.
    /// Format: "field1 asc, field2 desc" or "field1, field2 desc"
    /// Default direction is ascending when not specified.
    /// </summary>
    private static ImmutableArray<OrderByClause>? ParseODataOrderBy(string? orderby, LayerDefinition layer)
    {
        if (string.IsNullOrWhiteSpace(orderby))
        {
            return null;
        }

        var clauses = new List<OrderByClause>();
        var parts = orderby.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                continue;
            }

            var fieldName = tokens[0].Trim();

            // Validate field name (alphanumeric and underscores only)
            if (!FieldNameRegex().IsMatch(fieldName))
            {
                throw new ArgumentException($"Invalid field name in $orderby: {fieldName}");
            }

            // Default to ascending, check for explicit direction
            var ascending = true;
            if (tokens.Length > 1)
            {
                var direction = tokens[1].Trim().ToLowerInvariant();
                if (direction == "desc")
                {
                    ascending = false;
                }
                else if (direction != "asc")
                {
                    throw new ArgumentException($"Invalid sort direction in $orderby: {direction}. Use 'asc' or 'desc'.");
                }
            }

            var fieldDefinition = layer.Fields.FirstOrDefault(f =>
                f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
            var resolvedField = fieldDefinition?.Name ?? fieldName;
            var fieldType = fieldDefinition?.Type;

            clauses.Add(new OrderByClause(resolvedField, ascending, fieldType));
        }

        return clauses.Count > 0 ? clauses.ToImmutableArray() : null;
    }

    /// <summary>
    /// Regex patterns for basic OData parsing helpers.
    /// </summary>
    [GeneratedRegex(@"name\s+eq\s+'([^']*)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LayerNameFilterRegex();

    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex FieldNameRegex();

}
