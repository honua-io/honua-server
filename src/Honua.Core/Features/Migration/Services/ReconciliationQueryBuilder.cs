// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Compiles a shared-target reconciliation predicate against the published target schema.
/// Dedicated import targets are probed in full so a predicate cannot conceal extra rows.
/// </summary>
/// <param name="metadata">Published target metadata.</param>
/// <param name="filters">Canonical filter parser and compiler, required only for shared-target filter mirrors.</param>
public sealed class ReconciliationQueryBuilder(IMetadataV2GraphProvider metadata, IFilterExpressionService? filters = null)
{
    /// <summary>Builds the common predicate for count, extent, and sample probes.</summary>
    public async Task<FeatureQuery> BuildAsync(LayerReconciliationLayerInput layer, CancellationToken cancellationToken = default)
    {
        if (layer.TargetContainsOnlyImportedFeatures || string.IsNullOrWhiteSpace(layer.FilterMirror))
        {
            return default;
        }

        if (filters is null)
        {
            throw new InvalidOperationException("Reconciliation filter translation is unavailable.");
        }

        var snapshot = await metadata.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (layer.TargetHonuaLayerId is not int layerId ||
            !snapshot.Index.StorageBindingsByStorageLayerId.TryGetValue(layerId, out var binding) ||
            !snapshot.Index.ResourcesById.TryGetValue(binding.ResourceId, out var resource))
        {
            throw new ArgumentException("Reconciliation target metadata is unavailable.");
        }

        var parsed = filters.Parse(FilterLanguage.ArcGisSql, layer.FilterMirror);
        if (!parsed.IsSuccess || parsed.Expression is null)
        {
            throw new ArgumentException("Invalid reconciliation filter.");
        }

        var expression = MapProperties(parsed.Expression, layer.FilterFieldMappings);
        var translated = filters.Translate(expression, resource);
        if (!translated.IsSuccess || translated.SqlFilter is null)
        {
            throw new ArgumentException("Unsupported reconciliation filter.");
        }

        return new FeatureQuery { SqlFilter = translated.SqlFilter };
    }

    private static FilterExpression MapProperties(FilterExpression expression, IReadOnlyDictionary<string, string>? mappings)
    {
        if (mappings is null || mappings.Count == 0)
        {
            return expression;
        }

        FilterExpression Map(FilterExpression node) => MapProperties(node, mappings);
        return expression switch
        {
            PropertyReference property => property with { PropertyName = MapName(property.PropertyName, mappings) },
            BinaryExpression binary => binary with { Left = Map(binary.Left), Right = Map(binary.Right) },
            UnaryExpression unary => unary with { Operand = Map(unary.Operand) },
            SpatialPredicate spatial => spatial with { Left = Map(spatial.Left), Right = Map(spatial.Right) },
            SpatialDistancePredicate distance => distance with
            {
                Left = Map(distance.Left), Right = Map(distance.Right), Distance = Map(distance.Distance)
            },
            TemporalPredicate temporal => temporal with { Left = Map(temporal.Left), Right = Map(temporal.Right) },
            ArrayPredicate array => array with { Left = Map(array.Left), Right = Map(array.Right) },
            FunctionCall function => function with { Arguments = function.Arguments.Select(Map).ToArray() },
            ArrayLiteral array => array with { Elements = array.Elements.Select(Map).ToArray() },
            ValueList list => list with { Values = list.Values.Select(Map).ToArray() },
            _ => expression
        };
    }

    private static string MapName(string name, IReadOnlyDictionary<string, string> mappings)
    {
        foreach (var mapping in mappings)
        {
            if (string.Equals(name, mapping.Key, StringComparison.OrdinalIgnoreCase))
            {
                return mapping.Value;
            }
        }

        return name;
    }
}
