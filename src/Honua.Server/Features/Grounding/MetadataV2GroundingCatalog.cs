// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Grounding;

internal static class MetadataV2GroundingCatalog
{
    public static IReadOnlyList<GroundingCatalogLayer> BuildLayers(MetadataV2GraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var layers = new List<GroundingCatalogLayer>();
        foreach (var resource in snapshot.Graph.Resources)
        {
            if (!IsDatasetResource(resource.Type))
            {
                continue;
            }

            var layerId = snapshot.ResolveStorageLayerId(resource);
            if (layerId is null)
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(resource.Metadata.Name)
                ? resource.Metadata.Id
                : resource.Metadata.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            layers.Add(new GroundingCatalogLayer(
                layerId.Value,
                name,
                resource.Metadata.Description,
                resource.ReadSrid(),
                resource.SchemaFields.Select(MapField).ToArray()));
        }

        return layers
            .OrderBy(layer => layer.LayerId)
            .ThenBy(layer => layer.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static GroundingCatalogField MapField(MetadataV2Field field)
        => new(
            field.Name,
            field.Type,
            field.Alias ?? field.Title ?? field.Description,
            field.Description,
            field.Nullable);

    private static bool IsDatasetResource(MetadataV2ResourceType type)
        => type is MetadataV2ResourceType.FeatureDataset
            or MetadataV2ResourceType.Table
            or MetadataV2ResourceType.RasterDataset
            or MetadataV2ResourceType.TileDataset;
}

internal sealed record GroundingCatalogLayer(
    int LayerId,
    string Name,
    string? Description,
    int? SpatialReferenceId,
    IReadOnlyList<GroundingCatalogField> Fields);

internal sealed record GroundingCatalogField(
    string Name,
    MetadataV2FieldType Type,
    string? Label,
    string? Description,
    bool Nullable)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Name : Label;

    public bool IsGeometry => Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography;

    public bool IsString => Type == MetadataV2FieldType.String;
}
