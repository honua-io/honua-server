// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.Immutable;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Immutable, thread-safe catalog of built-in geoprocessing operations seeded
/// from the server's existing geometry and spatial analytics capabilities.
/// </summary>
internal sealed class BuiltInProcessCatalog : IProcessCatalog
{
    private readonly FrozenDictionary<string, ProcessDefinition> _processes;
    private readonly ImmutableArray<ProcessDefinition> _all;
    private readonly FrozenDictionary<string, ImmutableArray<ProcessDefinition>> _byCategory;

    public BuiltInProcessCatalog(ILogger<BuiltInProcessCatalog>? logger = null)
    {
        var definitions = BuildDefinitions();
        _all = definitions.ToImmutableArray();
        _processes = definitions.ToFrozenDictionary(d => d.ProcessId, StringComparer.Ordinal);
        _byCategory = definitions
            .GroupBy(d => d.Category, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.ToImmutableArray(), StringComparer.Ordinal);

        GeoprocessingServiceLog.ProcessCatalogLoaded(logger ?? NullLogger<BuiltInProcessCatalog>.Instance, _all.Length);
    }

    public ProcessDefinition? GetProcess(string processId)
        => _processes.GetValueOrDefault(processId);

    public IReadOnlyList<ProcessDefinition> ListProcesses()
        => _all;

    public IReadOnlyList<ProcessDefinition> GetProcessesByCategory(string category)
        => _byCategory.TryGetValue(category, out var list) ? list : ImmutableArray<ProcessDefinition>.Empty;

    private static ProcessDefinition[] BuildDefinitions() =>
    [
        // -----------------------------------------------------------------------
        // Geometry operations (14)
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "geometry.buffer",
            Title = "Buffer",
            Description = "Creates a polygon at a specified distance around each input geometry.",
            Category = "geometry",
            Parameters =
            [
                Param("wkb", "Input Geometry", "Geometry to buffer as base64-encoded WKB.", ProcessParameterValueType.Wkb, required: true),
                Param("srid", "Spatial Reference", "SRID of the input geometry.", ProcessParameterValueType.Srid, required: true),
                Param("distance", "Buffer Distance", "Buffer distance in meters.", ProcessParameterValueType.FloatingPoint, required: true),
                Param("geodesic", "Geodesic", "Use geodesic (geography-based) buffering.", ProcessParameterValueType.Flag, defaultValue: "false"),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "geometry.simplify",
            Title = "Simplify",
            Description = "Generalizes a geometry by removing vertices within the given tolerance using the Douglas-Peucker algorithm. By default uses topology-preserving simplification (ST_SimplifyPreserveTopology); setting preserveTopology=false enables the faster plain Douglas-Peucker walk.",
            Category = "geometry",
            Parameters =
            [
                Param("wkb", "Input Geometry", "Geometry to simplify as base64-encoded WKB.", ProcessParameterValueType.Wkb, required: true),
                Param("srid", "Spatial Reference", "SRID of the input geometry.", ProcessParameterValueType.Srid, required: true),
                Param("tolerance", "Tolerance", "Simplification tolerance in spatial reference units; vertices within the tolerance of the simplified path are removed.", ProcessParameterValueType.FloatingPoint, required: true),
                Param("preserveTopology", "Preserve Topology", "Use topology-preserving simplification to avoid introducing self-intersections.", ProcessParameterValueType.Flag, defaultValue: "true"),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "geometry.project",
            Title = "Project",
            Description = "Reprojects geometries from one spatial reference to another.",
            Category = "geometry",
            Parameters =
            [
                Param("wkb", "Input Geometry", "Geometry to reproject as base64-encoded WKB.", ProcessParameterValueType.Wkb, required: true),
                Param("fromSrid", "From SRID", "Source spatial reference identifier.", ProcessParameterValueType.Srid, required: true),
                Param("toSrid", "To SRID", "Target spatial reference identifier.", ProcessParameterValueType.Srid, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "geometry.make-valid",
            Title = "Make Valid",
            Description = "Repairs invalid geometries by fixing self-intersections, duplicate vertices, and ring orientation.",
            Category = "geometry",
            Parameters =
            [
                Param("wkb", "Input Geometry", "Geometry to repair as base64-encoded WKB.", ProcessParameterValueType.Wkb, required: true),
                Param("srid", "Spatial Reference", "SRID of the input geometry.", ProcessParameterValueType.Srid, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "geometry.union",
            Title = "Union",
            Description = "Merges multiple geometries into a single geometry representing their spatial union.",
            Category = "geometry",
            Parameters =
            [
                Param("wkbs", "Input Geometries", "Array of geometries to union as base64-encoded WKB strings.", ProcessParameterValueType.WkbArray, required: true),
                Param("srid", "Spatial Reference", "SRID of the input geometries.", ProcessParameterValueType.Srid, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "geometry.intersect",
            Title = "Intersect",
            Description = "Computes the geometric intersection of a target geometry with an intersector geometry.",
            Category = "geometry",
            Parameters =
            [
                Param("targetWkb", "Target Geometry", "Target geometry as base64-encoded WKB.", ProcessParameterValueType.Wkb, required: true),
                Param("intersectorWkb", "Intersector Geometry", "Intersector geometry as base64-encoded WKB.", ProcessParameterValueType.Wkb, required: true),
                Param("srid", "Spatial Reference", "SRID of both geometries.", ProcessParameterValueType.Srid, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "geometry.clip",
            Title = "Clip",
            Description = "Clips a target geometry to the bounding envelope of a clipping geometry.",
            Category = "geometry",
            Parameters =
            [
                Param("targetWkb", "Target Geometry", "Target geometry as base64-encoded WKB.", ProcessParameterValueType.Wkb, required: true),
                Param("clipEnvelopeWkb", "Clip Envelope", "Clipping geometry whose bounding envelope is used, provided as base64-encoded WKB.", ProcessParameterValueType.Wkb, required: true),
                Param("srid", "Spatial Reference", "SRID of both geometries.", ProcessParameterValueType.Srid, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "geometry.difference",
            Title = "Difference",
            Description = "Subtracts an eraser geometry from a target geometry, returning the remaining portion.",
            Category = "geometry",
            Parameters =
            [
                Param("targetWkb", "Target Geometry", "Target geometry as base64-encoded WKB.", ProcessParameterValueType.Wkb, required: true),
                Param("eraserWkb", "Eraser Geometry", "Geometry to subtract as base64-encoded WKB.", ProcessParameterValueType.Wkb, required: true),
                Param("srid", "Spatial Reference", "SRID of both geometries.", ProcessParameterValueType.Srid, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "geometry.area",
            Title = "Area",
            Description = "Computes the geodesic area of a polygon geometry in square meters.",
            Category = "geometry",
            Parameters =
            [
                Param("wkb", "Input Geometry", "Polygon geometry as base64-encoded WKB.", ProcessParameterValueType.Wkb, required: true),
                Param("srid", "Spatial Reference", "SRID of the input geometry.", ProcessParameterValueType.Srid, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.Scalar]
        },
        new ProcessDefinition
        {
            ProcessId = "geometry.length",
            Title = "Length",
            Description = "Computes the geodesic length of a line geometry in meters.",
            Category = "geometry",
            Parameters =
            [
                Param("wkb", "Input Geometry", "Line geometry as base64-encoded WKB.", ProcessParameterValueType.Wkb, required: true),
                Param("srid", "Spatial Reference", "SRID of the input geometry.", ProcessParameterValueType.Srid, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.Scalar]
        },
        new ProcessDefinition
        {
            ProcessId = "geometry.centroid",
            Title = "Centroid",
            Description = "Computes the centroid point of the input geometry. For collections the centroid is computed over all member vertices.",
            Category = "geometry",
            Parameters =
            [
                Param("wkb", "Input Geometry", "Geometry as base64-encoded WKB.", ProcessParameterValueType.Wkb, required: true),
                Param("srid", "Spatial Reference", "SRID of the input geometry.", ProcessParameterValueType.Srid, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "geometry.convex-hull",
            Title = "Convex Hull",
            Description = "Computes the convex hull of the input geometry. For collections the hull is computed over all member vertices, matching PostGIS ST_ConvexHull semantics.",
            Category = "geometry",
            Parameters =
            [
                Param("wkb", "Input Geometry", "Geometry as base64-encoded WKB.", ProcessParameterValueType.Wkb, required: true),
                Param("srid", "Spatial Reference", "SRID of the input geometry.", ProcessParameterValueType.Srid, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "geometry.dissolve",
            Title = "Dissolve",
            Description = "Collapses adjacent same-attribute geometries into one feature per group. Each input geometry is paired with an optional groupKeys entry; geometries sharing a key are unioned. When groupKeys is omitted every input collapses into a single feature, mirroring geometry.union but emitting a FeatureCollection envelope so downstream consumers can rely on the same shape regardless of group cardinality.",
            Category = "geometry",
            Parameters =
            [
                Param("wkbs", "Input Geometries", "Array of geometries to dissolve as base64-encoded WKB strings.", ProcessParameterValueType.WkbArray, required: true),
                Param("srid", "Spatial Reference", "SRID of the input geometries.", ProcessParameterValueType.Srid, required: true),
                Param("groupKeys", "Group Keys", "Optional JSON array of attribute keys parallel to 'wkbs'; geometries sharing a key dissolve together. When omitted all inputs collapse into a single feature.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "geometry.snap",
            Title = "Snap",
            Description = "Snaps the vertices of an input geometry to those of a reference geometry whenever the distance between two candidates falls within the supplied tolerance, using the NetTopologySuite GeometrySnapper. Useful for aligning adjacent dataset boundaries before overlay operations that would otherwise produce sliver polygons.",
            Category = "geometry",
            Parameters =
            [
                Param("wkb", "Input Geometry", "Geometry whose vertices will be snapped, as base64-encoded WKB.", ProcessParameterValueType.Wkb, required: true),
                Param("referenceWkb", "Reference Geometry", "Reference geometry whose vertices are the snap targets, as base64-encoded WKB.", ProcessParameterValueType.Wkb, required: true),
                Param("srid", "Spatial Reference", "SRID of both geometries.", ProcessParameterValueType.Srid, required: true),
                Param("tolerance", "Tolerance", "Snap tolerance in spatial reference units; input vertices within this distance of a reference vertex are pulled onto it.", ProcessParameterValueType.FloatingPoint, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },

        // -----------------------------------------------------------------------
        // Spatial analytics operations (4)
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "analytics.cluster",
            Title = "Spatial Clustering",
            Description = "Groups features into spatial clusters using DBSCAN or K-Means algorithms.",
            Category = "analytics",
            Parameters =
            [
                Param("layerId", "Layer", "Target layer identifier.", ProcessParameterValueType.LayerId, required: true),
                Param("algorithm", "Algorithm", "Clustering algorithm. Allowed values: dbscan, kmeans. Defaults to dbscan.", ProcessParameterValueType.Text),
                Param("eps", "Epsilon", "Maximum distance between neighbors for DBSCAN, in meters. Must be > 0. Required when algorithm is dbscan.", ProcessParameterValueType.FloatingPoint),
                Param("minPoints", "Min Points", "Minimum cluster size for DBSCAN. Must be ≥ 1. Required when algorithm is dbscan.", ProcessParameterValueType.WholeNumber),
                Param("k", "K", "Number of clusters for KMeans. Must be ≥ 1. Required when algorithm is kmeans.", ProcessParameterValueType.WholeNumber),
                Param("returnHullPerCluster", "Return Hull", "Return convex hull polygon per cluster instead of labeled points.", ProcessParameterValueType.Flag, defaultValue: "false"),
                Param("outStatistics", "Out Statistics", "GeoServices statistics payload aggregated over each cluster. Requires returnHullPerCluster=true; per-feature output cannot carry aggregate columns.", ProcessParameterValueType.Text),
                .. SharedAnalyticsFilterParameters,
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer, ArtifactKind.Table]
        },
        new ProcessDefinition
        {
            ProcessId = "analytics.spatial-join",
            Title = "Spatial Join",
            Description = "Enriches target features with attributes or aggregates from a join layer based on a spatial predicate.",
            Category = "analytics",
            Parameters =
            [
                Param("layerId", "Target Layer", "Target layer identifier.", ProcessParameterValueType.LayerId, required: true),
                Param("joinLayerId", "Join Layer", "Join layer identifier.", ProcessParameterValueType.LayerId, required: true),
                Param("predicate", "Predicate", "Spatial predicate. Allowed values: intersects, contains, within, dwithin. Defaults to intersects.", ProcessParameterValueType.Text),
                Param("distance", "Distance", "Distance threshold in meters. Must be > 0. Required when predicate is dwithin.", ProcessParameterValueType.FloatingPoint),
                Param("carryFields", "Carry Fields", "Comma-separated join-layer columns whose matched values are emitted as arrays on each target feature.", ProcessParameterValueType.Text),
                Param("outStatistics", "Out Statistics", "GeoServices statistics payload aggregated over the matched join rows for each target feature.", ProcessParameterValueType.Text),
                .. SharedAnalyticsFilterParameters,
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer, ArtifactKind.Table]
        },
        new ProcessDefinition
        {
            ProcessId = "analytics.buffer-aggregate",
            Title = "Buffer Aggregate",
            Description = "Buffers features by a fixed distance and optionally dissolves per group with aggregate statistics.",
            Category = "analytics",
            Parameters =
            [
                Param("layerId", "Layer", "Target layer identifier.", ProcessParameterValueType.LayerId, required: true),
                Param("distance", "Distance", "Buffer distance value in the supplied unit. Must be ≥ 0; the maximum cap is enforced after unit conversion.", ProcessParameterValueType.FloatingPoint, required: true),
                Param("unit", "Unit", "Distance unit. Allowed values: meters, kilometers, feet, miles.", ProcessParameterValueType.Text, defaultValue: "meters"),
                Param("dissolve", "Dissolve", "Dissolve overlapping buffers.", ProcessParameterValueType.Flag, defaultValue: "true"),
                Param("groupByFields", "Group By Fields", "Comma-separated columns used to group dissolved buffers; one row is emitted per group.", ProcessParameterValueType.Text),
                Param("outStatistics", "Out Statistics", "GeoServices statistics payload aggregated per group. Requires dissolve=true; per-feature output cannot carry aggregate columns.", ProcessParameterValueType.Text),
                .. SharedAnalyticsFilterParameters,
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "analytics.density",
            Title = "Density Binning",
            Description = "Bins features into a hex or square grid and returns cell counts or weighted sums.",
            Category = "analytics",
            Parameters =
            [
                Param("layerId", "Layer", "Target layer identifier.", ProcessParameterValueType.LayerId, required: true),
                Param("mode", "Bin Mode", "Binning mode. Allowed values: hex, square. Defaults to hex.", ProcessParameterValueType.Text),
                Param("cellSize", "Cell Size", "Grid cell size in meters. Must be > 0.", ProcessParameterValueType.FloatingPoint, required: true),
                Param("weightField", "Weight Field", "Optional field name for weighted sums instead of counts.", ProcessParameterValueType.Text),
                .. SharedAnalyticsFilterParameters,
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer, ArtifactKind.Table]
        },

        // -----------------------------------------------------------------------
        // Surface-analysis operations (6)
        // DEM-derived raster products. These are catalog-first declarations only;
        // heavyweight execution still flows through the canonical job/runtime path.
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "surface.slope",
            Title = "Slope",
            Description = "Computes a slope raster from an elevation surface using PostGIS ST_Slope.",
            Category = "surface",
            Parameters =
            [
                .. SharedRasterSourceParameters,
                Param("units", "Units", "Slope units. Allowed values: degrees, percent, radians. Defaults to degrees.", ProcessParameterValueType.Text, defaultValue: "degrees"),
                Param("zFactor", "Z Factor", "Vertical-to-horizontal scale factor. Must be > 0. Defaults to 1.0.", ProcessParameterValueType.FloatingPoint, defaultValue: "1.0"),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster]
        },
        new ProcessDefinition
        {
            ProcessId = "surface.aspect",
            Title = "Aspect",
            Description = "Computes a compass-bearing aspect raster from an elevation surface using PostGIS ST_Aspect.",
            Category = "surface",
            Parameters =
            [
                .. SharedRasterSourceParameters,
            ],
            OutputArtifactKinds = [ArtifactKind.Raster]
        },
        new ProcessDefinition
        {
            ProcessId = "surface.hillshade",
            Title = "Hillshade",
            Description = "Computes a hillshade raster using illumination azimuth, altitude, and vertical scale inputs.",
            Category = "surface",
            Parameters =
            [
                .. SharedRasterSourceParameters,
                Param("azimuth", "Azimuth", "Illumination azimuth in degrees clockwise from north. Must be between 0 and 360. Defaults to 315.", ProcessParameterValueType.FloatingPoint, defaultValue: "315"),
                Param("altitude", "Altitude", "Illumination altitude above the horizon in degrees. Must be between 0 and 90. Defaults to 45.", ProcessParameterValueType.FloatingPoint, defaultValue: "45"),
                Param("zFactor", "Z Factor", "Vertical-to-horizontal scale factor. Must be > 0. Defaults to 1.0.", ProcessParameterValueType.FloatingPoint, defaultValue: "1.0"),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster]
        },
        new ProcessDefinition
        {
            ProcessId = "surface.rugosity-tri",
            Title = "Terrain Ruggedness Index",
            Description = "Computes a terrain ruggedness index raster using PostGIS ST_TRI. The current canonical implementation supports only a 3x3 neighborhood (windowRadius=1).",
            Category = "surface",
            Parameters =
            [
                .. SharedRasterSourceParameters,
                Param("windowRadius", "Window Radius", "Neighborhood radius in pixels. Must currently be 1.", ProcessParameterValueType.WholeNumber, defaultValue: "1"),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster]
        },
        new ProcessDefinition
        {
            ProcessId = "surface.rugosity-tpi",
            Title = "Topographic Position Index",
            Description = "Computes a topographic position index raster using PostGIS ST_TPI. The current canonical implementation supports only a 3x3 neighborhood (windowRadius=1).",
            Category = "surface",
            Parameters =
            [
                .. SharedRasterSourceParameters,
                Param("windowRadius", "Window Radius", "Neighborhood radius in pixels. Must currently be 1.", ProcessParameterValueType.WholeNumber, defaultValue: "1"),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster]
        },
        new ProcessDefinition
        {
            ProcessId = "surface.roughness",
            Title = "Roughness",
            Description = "Computes a roughness raster using PostGIS ST_Roughness. The current canonical implementation supports only a 3x3 neighborhood (windowRadius=1).",
            Category = "surface",
            Parameters =
            [
                .. SharedRasterSourceParameters,
                Param("windowRadius", "Window Radius", "Neighborhood radius in pixels. Must currently be 1.", ProcessParameterValueType.WholeNumber, defaultValue: "1"),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster]
        },

        // -----------------------------------------------------------------------
        // Raster operations (5)
        // Raster analysis and mutation workflows surfaced through the seeded
        // process catalog rather than a separate discovery plane.
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "raster.clip",
            Title = "Clip Raster",
            Description = "Clips a raster to the supplied boundary geometry.",
            Category = "raster",
            Parameters =
            [
                .. SharedRasterSourceParameters,
                Param("boundary", "Boundary", "Clip boundary geometry in WKB format.", ProcessParameterValueType.Wkb, required: true),
                Param("boundarySrid", "Boundary SRID", "Spatial reference identifier of the boundary geometry when it differs from the raster SRID.", ProcessParameterValueType.Srid),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster]
        },
        new ProcessDefinition
        {
            ProcessId = "raster.reproject",
            Title = "Reproject Raster",
            Description = "Reprojects a raster into a new spatial reference using the requested resampling algorithm.",
            Category = "raster",
            Parameters =
            [
                .. SharedRasterSourceParameters,
                Param("targetSrid", "Target SRID", "Target spatial reference identifier.", ProcessParameterValueType.Srid, required: true),
                Param("resampling", "Resampling", "Resampling algorithm. Allowed values: nearestneighbor, bilinear, cubic, lanczos. Defaults to bilinear.", ProcessParameterValueType.Text, defaultValue: "bilinear"),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster]
        },
        new ProcessDefinition
        {
            ProcessId = "raster.statistics",
            Title = "Raster Statistics",
            Description = "Computes per-band statistics for a raster. Band selection is optional and uses a comma-separated list.",
            Category = "raster",
            Parameters =
            [
                .. SharedRasterSourceParameters,
                Param("bands", "Bands", "Optional comma-separated 1-based band numbers to analyze. When omitted, all bands are analyzed.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.Scalar]
        },
        new ProcessDefinition
        {
            ProcessId = "raster.histogram",
            Title = "Raster Histogram",
            Description = "Computes per-band histograms for a raster.",
            Category = "raster",
            Parameters =
            [
                .. SharedRasterSourceParameters,
                Param("bands", "Bands", "Optional comma-separated 1-based band numbers to analyze. When omitted, all bands are analyzed.", ProcessParameterValueType.Text),
                Param("binCount", "Bin Count", "Histogram bin count. Must be a positive integer. Defaults to 256.", ProcessParameterValueType.WholeNumber, defaultValue: "256"),
            ],
            OutputArtifactKinds = [ArtifactKind.Scalar]
        },
        new ProcessDefinition
        {
            ProcessId = "raster.zonal-statistics",
            Title = "Zonal Statistics",
            Description = "Computes zonal aggregates by intersecting a raster with polygonal zones from another layer.",
            Category = "raster",
            Parameters =
            [
                .. SharedRasterSourceParameters,
                Param("zonesLayerId", "Zones Layer", "Layer identifier whose feature geometries define the aggregation zones.", ProcessParameterValueType.LayerId, required: true),
                Param("band", "Band", "1-based raster band to aggregate. Defaults to 1.", ProcessParameterValueType.WholeNumber, defaultValue: "1"),
                Param("statistics", "Statistics", "Comma-separated stat names. Allowed values: count, sum, mean, min, max, stddev, variance.", ProcessParameterValueType.Text, defaultValue: "count,mean,stddev,min,max,sum"),
            ],
            OutputArtifactKinds = [ArtifactKind.Table]
        },

        // -----------------------------------------------------------------------
        // Conversion operations (4)
        // Explicit format/CRS conversion idioms so adapters can expose them
        // without inventing a second semantic layer.
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "conversion.geometry-format",
            Title = "Geometry Format Conversion",
            Description = "Converts a geometry into another interchange format such as WKT, GeoJSON, WKB, or EWKT.",
            Category = "conversion",
            Parameters =
            [
                Param("geometry", "Geometry", "Input geometry in WKB format.", ProcessParameterValueType.Wkb, required: true),
                Param("target", "Target Format", "Target geometry encoding. Allowed values: wkt, geojson, wkb, ewkt.", ProcessParameterValueType.Text, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.Scalar]
        },
        new ProcessDefinition
        {
            ProcessId = "conversion.feature-project",
            Title = "Project Feature Layer",
            Description = "Reprojects every feature in a layer into a target spatial reference.",
            Category = "conversion",
            Parameters =
            [
                Param("layerId", "Layer", "Target layer identifier.", ProcessParameterValueType.LayerId, required: true),
                Param("targetSrid", "Target SRID", "Target spatial reference identifier.", ProcessParameterValueType.Srid, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "conversion.raster-format",
            Title = "Raster Format Conversion",
            Description = "Exports a raster into another raster format such as GTiff, PNG, JPEG, or COG.",
            Category = "conversion",
            Parameters =
            [
                .. SharedRasterSourceParameters,
                Param("targetFormat", "Target Format", "Target raster format. Allowed values: GTiff, PNG, JPEG, COG.", ProcessParameterValueType.Text, required: true),
                Param("compression", "Compression", "Optional format-specific compression hint.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster]
        },
        new ProcessDefinition
        {
            ProcessId = "conversion.raster-reproject",
            Title = "Raster CRS Conversion",
            Description = "Exports a raster into another spatial reference as an explicit conversion workflow.",
            Category = "conversion",
            Parameters =
            [
                .. SharedRasterSourceParameters,
                Param("targetSrid", "Target SRID", "Target spatial reference identifier.", ProcessParameterValueType.Srid, required: true),
                Param("resampling", "Resampling", "Resampling algorithm. Allowed values: nearestneighbor, bilinear, cubic, lanczos. Defaults to bilinear.", ProcessParameterValueType.Text, defaultValue: "bilinear"),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster]
        },

        // -----------------------------------------------------------------------
        // Generalization operations (2)
        // Layer-level counterparts of the geometry-scoped operations: apply a
        // generalization transform across every geometry in a target layer.
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "generalization.simplify-layer",
            Title = "Simplify Layer",
            Description = "Applies topology-aware simplification across every geometry in a layer. The tolerance is expressed in the layer's spatial-reference units (degrees for geographic, meters for projected), matching geometry.simplify; it is not forced to meters like analytics.density.cellSize.",
            Category = "generalization",
            Parameters =
            [
                Param("layerId", "Layer", "Target layer identifier.", ProcessParameterValueType.LayerId, required: true),
                Param("tolerance", "Tolerance", "Simplification tolerance in the layer's spatial-reference units (degrees for geographic, meters for projected). Must be > 0.", ProcessParameterValueType.FloatingPoint, required: true),
                Param("preserveTopology", "Preserve Topology", "Use topology-preserving simplification (ST_SimplifyPreserveTopology). Defaults to true.", ProcessParameterValueType.Flag, defaultValue: "true"),
                .. SharedAnalyticsFilterParameters,
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "generalization.dissolve",
            Title = "Dissolve",
            Description = "Dissolves (unions) features by optional attribute group, producing one feature per group and optional aggregate statistics over the dissolved rows. Unlike analytics.buffer-aggregate, no buffer is applied before the union.",
            Category = "generalization",
            Parameters =
            [
                Param("layerId", "Layer", "Target layer identifier.", ProcessParameterValueType.LayerId, required: true),
                Param("groupByFields", "Group By Fields", "Comma-separated attribute columns used to group dissolved features; one row is emitted per group. When empty, all features dissolve into a single geometry.", ProcessParameterValueType.Text),
                Param("dissolve", "Dissolve", "Union the grouped geometries. Defaults to true.", ProcessParameterValueType.Flag, defaultValue: "true"),
                Param("outStatistics", "Out Statistics", "GeoServices statistics payload aggregated per group. Requires dissolve=true; per-feature output cannot carry aggregate columns.", ProcessParameterValueType.Text),
                .. SharedAnalyticsFilterParameters,
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer, ArtifactKind.Table]
        },

        // -----------------------------------------------------------------------
        // Data-management operations (3)
        // Bulk, layer-level mutation workflows. delete-features and
        // calculate-field are destructive and route through the existing
        // approval gate via the destructive-process classifier. copy-features
        // is non-destructive but still governed by the same canonical runtime.
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "data-management.copy-features",
            Title = "Copy Features",
            Description = "Copies features (optionally filtered) from a source layer into a new target layer. Non-destructive — the source layer is not modified.",
            Category = "data-management",
            Parameters =
            [
                Param("sourceLayerId", "Source Layer", "Source layer identifier.", ProcessParameterValueType.LayerId, required: true),
                Param("targetLayerName", "Target Layer Name", "Name of the new layer that will hold the copied features.", ProcessParameterValueType.Text, required: true),
                Param("where", "Where", "Optional ArcGIS SQL filter applied to the source layer.", ProcessParameterValueType.Text),
                Param("objectIds", "Object IDs", "Optional comma-separated feature identifiers to limit the copy to.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "data-management.delete-features",
            Title = "Delete Features",
            Description = "Deletes features matching a filter from a source layer. Destructive — requires approval. At least one of 'where' or 'objectIds' must be supplied to prevent unbounded deletion.",
            Category = "data-management",
            Parameters =
            [
                Param("layerId", "Layer", "Target layer identifier.", ProcessParameterValueType.LayerId, required: true),
                Param("where", "Where", "ArcGIS SQL filter selecting features to delete. At least one of 'where' or 'objectIds' is required.", ProcessParameterValueType.Text),
                Param("objectIds", "Object IDs", "Comma-separated feature identifiers to delete. At least one of 'where' or 'objectIds' is required.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.Scalar]
        },
        new ProcessDefinition
        {
            ProcessId = "data-management.calculate-field",
            Title = "Calculate Field",
            Description = "Sets a field value on matching features using a constant or SQL expression. Destructive — requires approval. Expressions are gated by the FeatureServer.Edits expression allow-list at execution time.",
            Category = "data-management",
            Parameters =
            [
                Param("layerId", "Layer", "Target layer identifier.", ProcessParameterValueType.LayerId, required: true),
                Param("fieldName", "Field Name", "Simple identifier naming the field to update (letters, digits, underscore; no dotted paths).", ProcessParameterValueType.Text, required: true),
                Param("expression", "Expression", "Constant or SQL expression evaluated per feature. Parsed by the same allow-listed expression gate FeatureServer.Edits.CalculateFieldValue uses.", ProcessParameterValueType.Text, required: true),
                Param("where", "Where", "Optional ArcGIS SQL filter selecting features to update.", ProcessParameterValueType.Text),
                Param("objectIds", "Object IDs", "Optional comma-separated feature identifiers to update.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.Scalar]
        },
    ];

    // Shared GeoServices-style filter inputs that every analytics handler honors via
    // AnalyticsFeatureQueryFactory. Declared here so the catalog matches the live
    // surface and ProcessPlanValidator does not reject plans the handlers accept.
    // Values are passed through as strings; the handler parses and rejects bad
    // shapes (SQL syntax, non-numeric SRID, distance-based spatialRel, etc.).
    private static readonly ProcessParameterSpec[] SharedAnalyticsFilterParameters =
    [
        Param("where", "Where", "ArcGIS SQL filter applied to the source layer.", ProcessParameterValueType.Text),
        Param("objectIds", "Object IDs", "Comma-separated feature identifiers to limit the analysis to.", ProcessParameterValueType.Text),
        Param("geometry", "Geometry Filter", "GeoServices geometry filter (envelope, polygon, etc.) restricting the input set.", ProcessParameterValueType.Text),
        Param("geometryType", "Geometry Type", "GeoServices geometry type for the geometry filter (e.g. esriGeometryEnvelope).", ProcessParameterValueType.Text),
        Param("inSR", "Input SR", "Spatial reference identifier of the geometry filter.", ProcessParameterValueType.Text),
        Param("spatialRel", "Spatial Relationship", "GeoServices spatial relationship (e.g. esriSpatialRelIntersects). Distance-based relationships are rejected here; use the operation-specific 'distance' or the 'where' clause instead.", ProcessParameterValueType.Text),
        Param("time", "Time Filter", "Temporal filter (instant or extent) following the FeatureServer time convention.", ProcessParameterValueType.Text),
        Param("timeRelation", "Time Relation", "Temporal predicate paired with the 'time' filter.", ProcessParameterValueType.Text),
    ];

    // Shared layer/raster selector used by surface, raster, and raster-conversion
    // families. `rasterId` is modeled as Text rather than WholeNumber so the
    // validator can admit full 64-bit ids instead of truncating to Int32.
    private static readonly ProcessParameterSpec[] SharedRasterSourceParameters =
    [
        Param("layerId", "Layer", "Target raster layer identifier.", ProcessParameterValueType.LayerId, required: true),
        Param("rasterId", "Raster", "Optional raster identifier. When omitted, the primary raster for the layer is used. When supplied, it must be a positive 64-bit integer.", ProcessParameterValueType.Text),
    ];

    private static ProcessParameterSpec Param(
        string name,
        string displayName,
        string description,
        ProcessParameterValueType valueType,
        bool required = false,
        string? defaultValue = null) => new()
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            ValueType = valueType,
            Required = required,
            DefaultValue = defaultValue
        };
}
