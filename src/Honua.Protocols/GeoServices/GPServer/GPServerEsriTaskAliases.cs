// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Protocols.GeoServices.GPServer;

// An alias only renames a published Job process for discovery and does not adapt its parameter contract to the same-named Esri tool, and a real catalog process id always wins over an alias.
internal static class GPServerEsriTaskAliases
{
    private static readonly FrozenDictionary<string, string> AliasByProcessId = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Geometry
        ["geometry.buffer"] = "Buffer",
        ["geometry.snap"] = "Snap",

        // Layer-aware overlay (Esri Analysis toolbox parity ops)
        ["overlay.clip"] = "Clip",
        ["overlay.intersect"] = "Intersect",
        ["overlay.union"] = "Union",
        ["overlay.erase"] = "Erase",
        ["overlay.merge"] = "Merge",
        ["overlay.split"] = "Split",

        // Proximity
        ["proximity.near"] = "Near",
        ["proximity.near-table"] = "GenerateNearTable",
        ["proximity.euclidean-distance"] = "EucDistance",
        ["proximity.euclidean-allocation"] = "EucAllocation",

        // Statistics
        ["statistics.summarize"] = "Statistics",
        ["statistics.frequency"] = "Frequency",

        // Surface (DEM-derived)
        ["surface.slope"] = "Slope",
        ["surface.aspect"] = "Aspect",
        ["surface.hillshade"] = "Hillshade",
        ["surface.contour"] = "Contour",
        ["surface.viewshed"] = "Viewshed",

        // Raster
        ["raster.reproject"] = "ProjectRaster",
        ["raster.statistics"] = "CalculateStatistics",
        ["raster.zonal-statistics"] = "ZonalStatisticsAsTable",
        ["raster.resample"] = "Resample",
        ["raster.interpolate-idw"] = "IDW",
        ["raster.interpolate-kriging"] = "Kriging",
        ["raster.mosaic"] = "MosaicToNewRaster",
        ["raster.reclassify"] = "Reclassify",

        // Imagery / ML (delegated cloud inference, #2241)
        ["imagery.classify"] = "ClassifyRaster",

        // Conversion
        ["conversion.feature-project"] = "Project",
        ["conversion.polygonize"] = "RasterToPolygon",
        ["conversion.rasterize"] = "FeatureToRaster",

        // Data management.
        // data-management.delete-features and data-management.calculate-field are
        // deliberately absent: both are ProtocolOnly operations (their only entry point
        // is the owning synchronous FeatureServer edit endpoint; no process-job executor
        // is registered), so GPServer never publishes them as tasks (#4409) and an alias
        // for them would promise a task that cannot be discovered or submitted (#4781).
        ["data-management.copy-features"] = "CopyFeatures",
        ["data-management.append"] = "Append",

        // Generalization
        ["generalization.dissolve"] = "Dissolve",

        // Analytics (job-dispatchable "managed" counterparts, since these are the
        // variants actually reachable through GPServer's submitJob/execute routes)
        ["analytics.spatial-join-managed"] = "SpatialJoin",
        ["analytics.hotspot-managed"] = "HotSpots",

        // Data enrichment (#2283): the async batch enrichment job maps onto the
        // Esri "Enrich Layer" analysis tool convention.
        ["enrichment.enrich"] = "EnrichLayer",
        ["routing.closest-facilities"] = "FindClosestFacilities",
        ["routing.od-cost-matrix"] = "GenerateOriginDestinationCostMatrix",
        ["routing.location-allocation"] = "SolveLocationAllocation",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> Contract => AliasByProcessId;

    private static readonly FrozenDictionary<string, string> ProcessIdByAlias =
        AliasByProcessId
            .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static string? GetAlias(string processId)
        => AliasByProcessId.GetValueOrDefault(processId);

    public static bool TryResolveProcessId(string taskName, out string processId)
        => ProcessIdByAlias.TryGetValue(taskName, out processId!);
}
