// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.SensorThings.Domain;

namespace Honua.Protocols.SensorThings.Services;

internal sealed record StaExpansion
{
    public required string Navigation { get; init; }
    public int Skip { get; init; }
    public int Top { get; init; }
    public string? WhereSql { get; init; }
    public required IReadOnlyList<object?> WhereParameters { get; init; }
    public required string OrderBySql { get; init; }
    public string? Select { get; init; }
    public string? Expand { get; init; }
    public bool Count { get; init; }
    public string? Filter { get; init; }
    public string? OrderBy { get; init; }
    public StaQueryPlan? NestedPlan { get; init; }
}

internal sealed partial class StaQueryPlan
{
    private static (List<StaProperty> Select, List<string> Navigations, StaQueryPlanResult? Failure) ParseSelectProperties(
        StaEntitySchema schema,
        string? rawSelect)
    {
        var select = new List<StaProperty>();
        var selectNavigations = new List<string>();
        if (rawSelect is null)
        {
            return (select, selectNavigations, null);
        }

        foreach (var name in rawSelect.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (name == "*")
            {
                select.AddRange(schema.Properties);
                selectNavigations.Add("@iot.selfLink");
                selectNavigations.AddRange(schema.NavigationProperties);
            }
            else if (string.Equals(name, "@iot.selfLink", StringComparison.OrdinalIgnoreCase))
                selectNavigations.Add("@iot.selfLink");
            else if (schema.TryGetProperty(name, out var property))
                select.Add(property);
            else if (schema.IsNavigationProperty(name))
                selectNavigations.Add(name);
            else
                return (select, selectNavigations, StaQueryPlanResult.BadRequest(
                    $"Property '{name}' cannot be selected on {schema.EntitySet}. Allowed: {schema.PropertyList}, {string.Join(", ", schema.NavigationProperties)}."));
        }

        return select.Count == 0 && selectNavigations.Count == 0
            ? (select, selectNavigations, StaQueryPlanResult.BadRequest("$select must name at least one property."))
            : (select, selectNavigations, null);
    }

    private static (List<StaExpansion> Expansions, StaQueryPlanResult? Failure) ParseAndMergeExpansions(
        StaEntitySchema schema,
        (string? RawExpand, int ExpansionDepth) input,
        StaFilterTranslator filterTranslator)
    {
        var expansions = new List<StaExpansion>();
        if (input.RawExpand is null)
        {
            return (expansions, null);
        }

        foreach (var item in SplitTopLevel(input.RawExpand, ','))
        {
            var expansion = ParseExpansion(schema, item, filterTranslator, input.ExpansionDepth, out var failure);
            if (expansion is null)
            {
                return (expansions, failure!.Value);
            }

            var duplicate = expansions.FindIndex(existing => existing.Navigation == expansion.Navigation);
            if (duplicate < 0)
            {
                expansions.Add(expansion);
                continue;
            }

            var (merged, mergeFailure) = MergeDuplicateExpansion(
                schema,
                (expansions[duplicate], expansion, input.ExpansionDepth),
                filterTranslator);
            if (mergeFailure is not null)
            {
                return (expansions, mergeFailure);
            }

            expansions[duplicate] = merged!;
        }

        return (expansions, null);
    }

    private static (StaExpansion? Merged, StaQueryPlanResult? Failure) MergeDuplicateExpansion(
        StaEntitySchema schema,
        (StaExpansion Existing, StaExpansion Incoming, int ExpansionDepth) merge,
        StaFilterTranslator filterTranslator)
    {
        var (existing, expansion, expansionDepth) = merge;
        if (existing.Skip != expansion.Skip || existing.Top != expansion.Top || existing.Filter != expansion.Filter ||
            existing.OrderBy != expansion.OrderBy || existing.Select != expansion.Select || existing.Count != expansion.Count)
        {
            return (null, StaQueryPlanResult.BadRequest($"Conflicting options for expanded navigation '{expansion.Navigation}'."));
        }

        var mergedExpand = string.Join(',', new[] { existing.Expand, expansion.Expand }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var relationship = SensorThingsRelationships.For(schema.EntitySet)[existing.Navigation];
        var mergedPlanResult = CreateFromOptionsAtDepth(
            new StaQueryOptions
            {
                Top = existing.Top,
                Skip = existing.Skip,
                Filter = existing.Filter,
                OrderBy = existing.OrderBy,
                Select = existing.Select,
                Expand = string.IsNullOrWhiteSpace(mergedExpand) ? null : mergedExpand,
                Count = existing.Count
            },
            (StaEntitySchema.For(relationship.Target), expansionDepth + 1),
            filterTranslator);
        if (!mergedPlanResult.IsSuccess)
        {
            return (null, mergedPlanResult);
        }

        return (existing with { Expand = mergedExpand, NestedPlan = mergedPlanResult.Plan }, null);
    }
}
