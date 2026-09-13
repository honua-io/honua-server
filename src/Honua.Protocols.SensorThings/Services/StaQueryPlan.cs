// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.SensorThings.Domain;
using Microsoft.AspNetCore.Http;

namespace Honua.Protocols.SensorThings.Services;

/// <summary>
/// One parsed <c>$expand</c> item: the navigation property and the nested options that
/// apply to it, already translated against the expanded entity's schema.
/// </summary>
/// <param name="Navigation">The navigation property name, e.g. <c>Observations</c>.</param>
/// <param name="Skip">Nested <c>$skip</c>.</param>
/// <param name="Top">Nested <c>$top</c>.</param>
/// <param name="WhereSql">Nested <c>$filter</c> translated to SQL, or null.</param>
/// <param name="WhereParameters">Parameters for <paramref name="WhereSql"/>.</param>
/// <param name="OrderBySql">Nested <c>$orderby</c> translated to an ORDER BY body.</param>
internal sealed record StaExpansion(
    string Navigation,
    int Skip,
    int Top,
    string? WhereSql,
    IReadOnlyList<object?> WhereParameters,
    string OrderBySql);

/// <summary>
/// A validated, translated request plan for one STA entity set: the paging options plus
/// the SQL fragments for <c>$filter</c> and <c>$orderby</c>, the <c>$select</c> projection,
/// and the <c>$expand</c> items.
/// </summary>
/// <remarks>
/// Building the plan is the point where an unsupported or malformed option becomes an
/// error instead of being dropped. Before #4201, <c>$filter</c>, <c>$select</c>,
/// <c>$expand</c> and the <c>$orderby</c> property were parsed and then discarded, so
/// <c>GET /Things?$filter=name eq 'nope'</c> answered 200 with every Thing and clients had
/// no way to detect the miss.
/// </remarks>
internal sealed class StaQueryPlan
{
    private StaQueryPlan(
        StaEntitySchema schema,
        StaQueryOptions options,
        string? whereSql,
        IReadOnlyList<object?> whereParameters,
        string orderBySql,
        IReadOnlyList<StaProperty> select,
        IReadOnlyList<string> selectNavigations,
        IReadOnlyList<StaExpansion> expansions)
    {
        Schema = schema;
        Options = options;
        WhereSql = whereSql;
        WhereParameters = whereParameters;
        OrderBySql = orderBySql;
        Select = select;
        SelectNavigations = selectNavigations;
        Expansions = expansions;
    }

    /// <summary>The entity set this plan targets.</summary>
    public StaEntitySchema Schema { get; }

    /// <summary>The raw system query options, including paging.</summary>
    public StaQueryOptions Options { get; }

    /// <summary>Translated <c>$filter</c>, or null when absent.</summary>
    public string? WhereSql { get; }

    /// <summary>Parameters bound to <see cref="WhereSql"/>.</summary>
    public IReadOnlyList<object?> WhereParameters { get; }

    /// <summary>Translated <c>$orderby</c> (always non-empty; the schema default when absent).</summary>
    public string OrderBySql { get; }

    /// <summary>Selected data properties; empty when <c>$select</c> was not supplied.</summary>
    public IReadOnlyList<StaProperty> Select { get; }

    /// <summary>Selected navigation properties; empty when <c>$select</c> was not supplied.</summary>
    public IReadOnlyList<string> SelectNavigations { get; }

    /// <summary>Parsed <c>$expand</c> items.</summary>
    public IReadOnlyList<StaExpansion> Expansions { get; }

    /// <summary>True when the request asked for a projection.</summary>
    public bool HasProjection => Options.Select is not null;

    /// <summary>The JSON members a projected entity keeps, in request order.</summary>
    public IReadOnlyList<string> ProjectedMembers =>
    [
        .. Select.Select(property => property.EmittedMember),
        .. SelectNavigations,
    ];

    /// <summary>The <c>$expand</c> item for <paramref name="navigation"/>, or null.</summary>
    public StaExpansion? Expansion(string navigation) =>
        Expansions.FirstOrDefault(item =>
            string.Equals(item.Navigation, navigation, StringComparison.OrdinalIgnoreCase));

    /// <summary>The catalog query this plan issues, including the fetch-one-extra page size.</summary>
    public CatalogQuery CatalogQuery =>
        new(WhereSql, WhereParameters, OrderBySql, Options.Skip, Options.FetchTop);

    /// <summary>The observation query this plan issues against <paramref name="datastreamId"/>.</summary>
    public ObservationQuery ObservationQuery(long? datastreamId) =>
        new(datastreamId, WhereSql, WhereParameters, OrderBySql, Options.Skip, Options.FetchTop);

    /// <summary>
    /// Parses and validates the request's system query options against
    /// <paramref name="schema"/>.
    /// </summary>
    public static StaQueryPlanResult Create(
        HttpRequest request,
        StaEntitySchema schema,
        StaFilterTranslator filterTranslator)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(filterTranslator);

        var options = StaQueryOptions.FromRequest(request);

        var filter = filterTranslator.Translate(schema, options.Filter);
        if (!filter.IsSuccess)
        {
            return StaQueryPlanResult.BadRequest(filter.Error ?? "Invalid $filter.");
        }

        var order = StaFilterTranslator.TranslateOrderBy(schema, options.OrderBy);
        if (!order.IsSuccess)
        {
            return StaQueryPlanResult.BadRequest(order.Error ?? "Invalid $orderby.");
        }

        var select = new List<StaProperty>();
        var selectNavigations = new List<string>();
        if (options.Select is { } rawSelect)
        {
            foreach (var name in rawSelect.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(name, "@iot.selfLink", StringComparison.OrdinalIgnoreCase))
                {
                    selectNavigations.Add("@iot.selfLink");
                    continue;
                }

                if (schema.TryGetProperty(name, out var property))
                {
                    select.Add(property);
                    continue;
                }

                if (schema.IsNavigationProperty(name))
                {
                    if (IsUnavailableNavigation(schema, name))
                    {
                        return StaQueryPlanResult.NotImplemented("FeaturesOfInterest are not exposed by this server.");
                    }

                    selectNavigations.Add(name);
                    continue;
                }

                return StaQueryPlanResult.BadRequest(
                    $"Property '{name}' cannot be selected on {schema.EntitySet}. Allowed: {schema.PropertyList}, {string.Join(", ", schema.NavigationProperties)}.");
            }

            if (select.Count == 0 && selectNavigations.Count == 0)
            {
                return StaQueryPlanResult.BadRequest("$select must name at least one property.");
            }
        }

        var expansions = new List<StaExpansion>();
        if (options.Expand is { } rawExpand)
        {
            foreach (var item in SplitTopLevel(rawExpand, ','))
            {
                var expansion = ParseExpansion(schema, item, filterTranslator, out var failure);
                if (expansion is null)
                {
                    return failure!.Value;
                }

                expansions.Add(expansion);
            }
        }

        return StaQueryPlanResult.Success(new StaQueryPlan(
            schema,
            options,
            filter.Sql,
            filter.Parameters,
            order.Sql ?? schema.DefaultOrderBySql,
            select,
            selectNavigations,
            expansions));
    }

    // Recognise this STA relationship so explicit requests receive 501, while never
    // projecting an empty selection or directing clients to a link we do not expose.
    private static bool IsUnavailableNavigation(StaEntitySchema schema, string name) =>
        schema == StaEntitySchema.Observations && name.Equals("FeatureOfInterest", StringComparison.OrdinalIgnoreCase);

    private static StaExpansion? ParseExpansion(
        StaEntitySchema schema,
        string item,
        StaFilterTranslator filterTranslator,
        out StaQueryPlanResult? failure)
    {
        failure = null;
        var name = item;
        string? nested = null;

        var open = item.IndexOf('(', StringComparison.Ordinal);
        if (open >= 0)
        {
            if (!item.EndsWith(')'))
            {
                failure = StaQueryPlanResult.BadRequest($"'{item}' is not a valid $expand item: unbalanced parentheses.");
                return null;
            }

            name = item[..open];
            nested = item[(open + 1)..^1];
        }

        name = name.Trim();
        if (!schema.IsNavigationProperty(name))
        {
            failure = StaQueryPlanResult.BadRequest(
                $"'{name}' is not a navigation property of {schema.EntitySet}. Allowed: {string.Join(", ", schema.NavigationProperties)}.");
            return null;
        }

        // Only the Datastream navigations are materialised. Everything else is declined
        // rather than dropped, so a client can tell the difference between "no related
        // entity" and "this server will not expand that".
        var expandedSchema = name switch
        {
            "Thing" => StaEntitySchema.Things,
            "Sensor" => StaEntitySchema.Sensors,
            "ObservedProperty" => StaEntitySchema.ObservedProperties,
            "Observations" => StaEntitySchema.Observations,
            _ => null,
        };

        if (IsUnavailableNavigation(schema, name))
        {
            failure = StaQueryPlanResult.NotImplemented("FeaturesOfInterest are not exposed by this server.");
            return null;
        }

        if (expandedSchema is null || schema.EntitySet != StaEntitySchema.Datastreams.EntitySet)
        {
            failure = StaQueryPlanResult.NotImplemented(
                $"$expand={name} is not supported on {schema.EntitySet}. This server expands Thing, Sensor, ObservedProperty and Observations on Datastreams; follow the entity's @iot.navigationLink otherwise.");
            return null;
        }

        var skip = 0;
        var top = StaQueryOptions.DefaultTop;
        string? nestedFilter = null;
        string? nestedOrderBy = null;

        foreach (var option in SplitTopLevel(nested ?? string.Empty, ';'))
        {
            var separator = option.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                failure = StaQueryPlanResult.BadRequest($"'{option}' is not a valid nested $expand option.");
                return null;
            }

            var key = option[..separator].Trim();
            var value = option[(separator + 1)..].Trim();
            switch (key.ToLowerInvariant())
            {
                case "$top":
                    if (!int.TryParse(value, out top) || top < 0)
                    {
                        failure = StaQueryPlanResult.BadRequest($"Nested $top '{value}' must be a non-negative integer.");
                        return null;
                    }

                    top = Math.Min(top, StaQueryOptions.MaxTop);
                    break;
                case "$skip":
                    if (!int.TryParse(value, out skip) || skip < 0)
                    {
                        failure = StaQueryPlanResult.BadRequest($"Nested $skip '{value}' must be a non-negative integer.");
                        return null;
                    }

                    break;
                case "$filter":
                    nestedFilter = value;
                    break;
                case "$orderby":
                    nestedOrderBy = value;
                    break;
                default:
                    failure = StaQueryPlanResult.NotImplemented(
                        $"Nested option '{key}' is not supported inside $expand. Supported: $top, $skip, $filter, $orderby.");
                    return null;
            }
        }

        var nestedFilterTranslation = filterTranslator.Translate(expandedSchema, nestedFilter);
        if (!nestedFilterTranslation.IsSuccess)
        {
            failure = StaQueryPlanResult.BadRequest(nestedFilterTranslation.Error ?? "Invalid nested $filter.");
            return null;
        }

        var nestedOrder = StaFilterTranslator.TranslateOrderBy(expandedSchema, nestedOrderBy);
        if (!nestedOrder.IsSuccess)
        {
            failure = StaQueryPlanResult.BadRequest(nestedOrder.Error ?? "Invalid nested $orderby.");
            return null;
        }

        return new StaExpansion(
            name,
            skip,
            top,
            nestedFilterTranslation.Sql,
            nestedFilterTranslation.Parameters,
            nestedOrder.Sql ?? expandedSchema.DefaultOrderBySql);
    }

    /// <summary>
    /// Splits on <paramref name="separator"/> at parenthesis depth zero and outside quoted
    /// literals, so <c>Observations($filter=name eq 'a,b')</c> stays one item.
    /// </summary>
    private static List<string> SplitTopLevel(string value, char separator)
    {
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return parts;
        }

        var depth = 0;
        var inQuotes = false;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (current == '\'')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && current == '(')
            {
                depth++;
            }
            else if (!inQuotes && current == ')')
            {
                depth--;
            }
            else if (!inQuotes && depth == 0 && current == separator)
            {
                AddPart(parts, value[start..i]);
                start = i + 1;
            }
        }

        AddPart(parts, value[start..]);
        return parts;

        static void AddPart(List<string> parts, string part)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                parts.Add(trimmed);
            }
        }
    }
}

/// <summary>
/// The outcome of building a <see cref="StaQueryPlan"/>: either the plan, or the status
/// code and message the protocol adapter must answer with. A recognised-but-unsupported
/// option is 501; a malformed one, or one naming an unknown property, is 400.
/// </summary>
internal readonly record struct StaQueryPlanResult(StaQueryPlan? Plan, int StatusCode, string? Error)
{
    /// <summary>True when a plan was produced.</summary>
    public bool IsSuccess => Plan is not null;

    public static StaQueryPlanResult Success(StaQueryPlan plan) => new(plan, 200, null);

    public static StaQueryPlanResult BadRequest(string error) => new(null, 400, error);

    public static StaQueryPlanResult NotImplemented(string error) => new(null, 501, error);
}
