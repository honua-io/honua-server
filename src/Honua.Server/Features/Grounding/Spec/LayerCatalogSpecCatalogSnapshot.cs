// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;

namespace Honua.Server.Features.Grounding.Spec;

internal sealed class LayerCatalogSpecCatalogSnapshot : ISpecCatalogSnapshot
{
    private readonly Dictionary<int, LayerDefinition> _layersById;
    private readonly Dictionary<string, LayerDefinition> _layersByName;

    public LayerCatalogSpecCatalogSnapshot(IReadOnlyList<LayerDefinition> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        Version = $"layers-{layers.Count}";
        _layersById = layers.ToDictionary(layer => layer.Id);
        _layersByName = layers
            .GroupBy(layer => layer.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    public string Version { get; }

    public TypeRef? ResolveSource(SourceBinding source)
    {
        var sourceRef = TryGetLiteral(source.Properties, "ref");
        if (string.IsNullOrWhiteSpace(sourceRef))
        {
            return null;
        }

        LayerDefinition? layer = null;
        if (TryParseCatalogLayerRef(sourceRef, out var layerId))
        {
            _layersById.TryGetValue(layerId, out layer);
        }
        else
        {
            _layersByName.TryGetValue(sourceRef, out layer);
        }

        if (layer is null)
        {
            return null;
        }

        var fields = layer.Fields
            .Where(field => !field.IsGeometry)
            .Select(field => field.Name)
            .ToArray();

        return new TypeRef(
            SpecTypeKind.Dataset,
            fields,
            layer.SpatialReference.Wkid > 0 ? $"EPSG:{layer.SpatialReference.Wkid}" : null);
    }

    private static string? TryGetLiteral(ObjectExpression expression, string key)
    {
        foreach (var field in expression.Fields)
        {
            if (string.Equals(field.Key, key, StringComparison.Ordinal) &&
                field.Value is LiteralNode literal &&
                literal.Kind == SpecTypeKind.String)
            {
                return literal.String;
            }
        }

        return null;
    }

    private static bool TryParseCatalogLayerRef(string value, out int layerId)
    {
        const string prefix = "catalog:layer:";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value[prefix.Length..], out layerId))
        {
            return true;
        }

        layerId = 0;
        return false;
    }
}
