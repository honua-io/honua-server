// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Protocols.GeoServices.GPServer;

/// <summary>
/// Additive Esri-conventional task-name overlay for the GPServer protocol adapter.
/// <para>
/// The canonical process catalog (<c>BuiltInProcessCatalog</c>) is process-ID-based
/// (e.g. <c>geometry.buffer</c>) per ADR-0029/ADR-0057's "one canonical engine, thin
/// protocol adapters" design. Esri/ArcGIS clients browsing or calling a GPServer task
/// list instead expect Esri-idiomatic tool names (e.g. <c>Buffer</c>). This lookup maps
/// internal process IDs to their real, documented Esri GP tool-name equivalent so the
/// GPServer <em>presentation</em> layer can publish both forms without changing the
/// engine's process-ID identity.
/// </para>
/// <para>
/// Only processes with an unambiguous, well-documented Esri GP tool analog are mapped.
/// Honua-specific operations (analytics/transform/source/sink/gdal/import/pcloud
/// pipelines, managed-runtime duplicates, and processes whose Esri semantics would
/// require guessing) intentionally keep only their internal-ID name. Where two internal
/// processes could plausibly share the same Esri name (e.g. the single-geometry
/// <c>geometry.*</c> primitives vs. the layer-aware <c>overlay.*</c>/<c>proximity.*</c>
/// counterparts), the alias is assigned to whichever process's parameter contract most
/// closely matches the real Esri tool (typically the layer/FeatureCollection-oriented
/// one, since Esri's Analysis/Data Management tools operate on feature classes) so every
/// alias resolves to exactly one process.
/// </para>
/// </summary>
internal static class GPServerEsriTaskAliases
{
    /// <summary>
    /// Internal process ID -&gt; Esri-conventional task name, for processes with an
    /// obvious, documented Esri GP tool equivalent.
    /// </summary>
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
        ["statistics.summarize"] = "SummaryStatistics",
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

        // Conversion
        ["conversion.feature-project"] = "Project",
        ["conversion.polygonize"] = "RasterToPolygon",
        ["conversion.rasterize"] = "FeatureToRaster",

        // Data management
        ["data-management.copy-features"] = "CopyFeatures",
        ["data-management.append"] = "Append",
        ["data-management.delete-features"] = "DeleteFeatures",
        ["data-management.calculate-field"] = "CalculateField",

        // Generalization
        ["generalization.dissolve"] = "Dissolve",

        // Analytics (job-dispatchable "managed" counterparts, since these are the
        // variants actually reachable through GPServer's submitJob/execute routes)
        ["analytics.spatial-join-managed"] = "SpatialJoin",
        ["analytics.hotspot-managed"] = "HotSpots",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Esri-conventional task name -&gt; internal process ID (the inverse of
    /// <see cref="AliasByProcessId"/>), for resolving task-info/submitJob/execute
    /// requests addressed by their Esri alias. Lookups are case-insensitive since
    /// ArcGIS client tooling is not always consistent about task-name casing.
    /// </summary>
    private static readonly FrozenDictionary<string, string> ProcessIdByAlias =
        AliasByProcessId
            .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the Esri-conventional alias for the given internal process ID, or
    /// <see langword="null"/> when the process has no documented Esri GP tool
    /// equivalent (in which case only the internal-ID name is published).
    /// </summary>
    public static string? GetAlias(string processId)
        => AliasByProcessId.GetValueOrDefault(processId);

    /// <summary>
    /// Resolves an Esri-conventional task name back to its internal process ID.
    /// Returns <see langword="false"/> when <paramref name="taskName"/> is not a
    /// known alias (it may still be a valid internal process ID, resolved
    /// separately by <c>IProcessCatalog.GetProcess</c>).
    /// </summary>
    public static bool TryResolveProcessId(string taskName, out string processId)
        => ProcessIdByAlias.TryGetValue(taskName, out processId!);
}
