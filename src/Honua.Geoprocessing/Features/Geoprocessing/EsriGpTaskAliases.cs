// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Geoprocessing;

/// <summary>Shared Esri GP task-name aliases over canonical process identifiers.</summary>
internal static class EsriGpTaskAliases
{
    private static readonly FrozenDictionary<string, string> AliasByProcessId = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["geometry.buffer"] = "Buffer",
        ["geometry.snap"] = "Snap",
        ["overlay.clip"] = "Clip",
        ["overlay.intersect"] = "Intersect",
        ["overlay.union"] = "Union",
        ["overlay.erase"] = "Erase",
        ["overlay.merge"] = "Merge",
        ["overlay.split"] = "Split",
        ["proximity.near"] = "Near",
        ["proximity.near-table"] = "GenerateNearTable",
        ["proximity.euclidean-distance"] = "EucDistance",
        ["proximity.euclidean-allocation"] = "EucAllocation",
        ["statistics.summarize"] = "Statistics",
        ["statistics.frequency"] = "Frequency",
        ["surface.slope"] = "Slope",
        ["surface.aspect"] = "Aspect",
        ["surface.hillshade"] = "Hillshade",
        ["surface.contour"] = "Contour",
        ["surface.viewshed"] = "Viewshed",
        ["raster.reproject"] = "ProjectRaster",
        ["raster.statistics"] = "CalculateStatistics",
        ["raster.zonal-statistics"] = "ZonalStatisticsAsTable",
        ["raster.resample"] = "Resample",
        ["raster.interpolate-idw"] = "IDW",
        ["raster.interpolate-kriging"] = "Kriging",
        ["raster.mosaic"] = "MosaicToNewRaster",
        ["raster.reclassify"] = "Reclassify",
        ["imagery.classify"] = "ClassifyRaster",
        ["conversion.feature-project"] = "Project",
        ["conversion.polygonize"] = "RasterToPolygon",
        ["conversion.rasterize"] = "FeatureToRaster",
        ["data-management.copy-features"] = "CopyFeatures",
        ["data-management.append"] = "Append",
        ["data-management.delete-features"] = "DeleteFeatures",
        ["data-management.calculate-field"] = "CalculateField",
        ["generalization.dissolve"] = "Dissolve",
        ["analytics.spatial-join-managed"] = "SpatialJoin",
        ["analytics.hotspot-managed"] = "HotSpots",
        ["enrichment.enrich"] = "EnrichLayer",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> ProcessIdByAlias = AliasByProcessId
        .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase)
        .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static string? GetAlias(string processId) => AliasByProcessId.GetValueOrDefault(processId);

    public static bool TryResolveProcessId(string taskName, out string processId)
        => ProcessIdByAlias.TryGetValue(taskName, out processId!);
}
