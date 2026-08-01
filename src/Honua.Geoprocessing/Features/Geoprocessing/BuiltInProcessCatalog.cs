// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.Immutable;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Geoprocessing;

/// <summary>
/// Immutable, thread-safe catalog of built-in geoprocessing operations seeded
/// from the server's existing geometry and spatial analytics capabilities.
/// </summary>
internal sealed class BuiltInProcessCatalog : IProcessCatalog
{
    public const string CatalogVersion = "honua.process_catalog.builtin.v1";

    /// <summary>
    /// Processes the catalog advertises for discoverability but whose executor fails EVERY job
    /// unconditionally in this build, keyed by process id with the reason as the value.
    /// <para>
    /// The plan validator deliberately admits these at submit time so the limitation surfaces
    /// as an explicit job failure rather than a silent absence from the catalog (see the
    /// <c>raster.interpolate-kriging</c> case in <see cref="ProcessPlanValidator"/> and
    /// <c>GdalRasterInterpolateJobExecutor.KrigingUnsupportedMessage</c>). That posture is
    /// right for submit, but tooling that <em>certifies</em> executability — the toolbox
    /// translation report, migration evidence — must not tell a migrating user a tool works
    /// when its executor can only fail, so it consults this set.
    /// </para>
    /// <para>
    /// Membership requires an UNCONDITIONAL failure. Processes that fail only because a
    /// deployment has not configured a backend (<c>imagery.classify</c>) are deliberately
    /// absent: a configured deployment runs them, and a static catalog cannot see that.
    /// </para>
    /// </summary>
    internal static readonly FrozenDictionary<string, string> AdvertisedButNotExecutableProcesses =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["raster.interpolate-kriging"] =
                "Process 'raster.interpolate-kriging' is advertised for discoverability only: no "
                + "kriging-capable numerical backend is bundled in this build, so every submitted "
                + "job fails. Use 'raster.interpolate-idw' for inverse-distance-weighted interpolation.",
        }.ToFrozenDictionary(StringComparer.Ordinal);

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
                Param("distance", "Buffer Distance", "Buffer distance in the input geometry's coordinate units (planar). For a geographic (degree) SRID the distance is in degrees, not meters; project to a metric CRS first for a metric buffer. Must be a finite number greater than zero.", ProcessParameterValueType.FloatingPoint, required: true),
                Param("geodesic", "Geodesic", "Use geodesic (geography-based) buffering. Not yet supported: submitting geodesic=true is rejected at plan validation.", ProcessParameterValueType.Flag, defaultValue: "false"),
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
            Description = "Computes the planar (Cartesian) area of a polygon geometry in the square of the input SRID's coordinate units. No geodesic conversion is performed: a geographic (degree) input yields area in squared degrees, not square meters — project to a metric CRS first for square-meter area.",
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
            Description = "Computes the planar (Cartesian) length of a line geometry (or perimeter of a polygon) in the input SRID's coordinate units. No geodesic conversion is performed: a geographic (degree) input yields length in degrees, not meters — project to a metric CRS first for a length in meters.",
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
                Param("eps", "Epsilon", "Maximum distance between neighbors for DBSCAN, in meters. Must be > 0. Required when algorithm is dbscan. For geographic layers the geometry is transformed to EPSG:3857 (Web Mercator) so eps is evaluated in meters there; those distances overstate ground distance by 1/cos(latitude) (~2x at 60°N).", ProcessParameterValueType.FloatingPoint),
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
                Param("predicate", "Predicate", "Spatial predicate evaluating join-vs-target. Allowed values: intersects (default), contains (the join geometry contains the target — point-in-polygon), within (the target contains the join geometry), dwithin.", ProcessParameterValueType.Text),
                Param("distance", "Distance", "Distance threshold in meters. Must be > 0. Required when predicate is dwithin. For geographic layers the geometry is transformed to EPSG:3857 (Web Mercator) so the threshold is evaluated in meters there; those distances overstate ground distance by 1/cos(latitude) (~2x at 60°N).", ProcessParameterValueType.FloatingPoint),
                Param("carryFields", "Carry Fields", "Comma-separated join-layer columns whose matched values are emitted as arrays on each target feature.", ProcessParameterValueType.Text),
                Param("outStatistics", "Out Statistics", "GeoServices statistics payload aggregated over the matched join rows for each target feature.", ProcessParameterValueType.Text),
                .. SharedAnalyticsFilterParameters,
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer, ArtifactKind.Table]
        },
        new ProcessDefinition
        {
            ProcessId = "analytics.spatial-join-managed",
            Title = "Spatial Join (Managed)",
            Description = "Job-executable, managed (NetTopologySuite) spatial join over two inline FeatureCollections. Distinct from analytics.spatial-join, which runs only synchronously through the layer-scoped PostGIS SpatialAnalytics protocol and is NOT job-dispatchable; this id is the workflow/codemod-reachable counterpart. For each target feature it summarizes EVERY matched join feature into per-target aggregates via an in-memory STRtree index — JOIN_COUNT plus optional numeric SUM/MEAN/MIN/MAX — preserving zero-match targets one-to-one. Pure managed overlay, no GDAL/GEOS dependency.",
            Category = "analytics",
            Parameters =
            [
                Param("input", "Target Features", "Target FeatureCollection as a data:application/geo+json;base64 data URI. Each target is preserved one-to-one with its match summary.", ProcessParameterValueType.Text, required: true),
                Param("join", "Join Features", "Join (reference) FeatureCollection as a data:application/geo+json;base64 data URI. Materialized into an in-memory STRtree spatial index.", ProcessParameterValueType.Text, required: true),
                Param("predicate", "Predicate", "Spatial predicate evaluating join-vs-target. Allowed values: intersects (default), contains (join geometry contains the target — point-in-polygon), within (target contains the join geometry).", ProcessParameterValueType.Text, defaultValue: "intersects"),
                Param("statistics", "Statistics", "Semicolon-separated 'field:stat' aggregates over matched join features. Supported stats: count (always emitted as JOIN_COUNT), sum, mean, min, max on numeric join fields (emitted as STAT_field). Example: 'pop:sum;pop:mean'.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "analytics.cluster-managed",
            Title = "Spatial Clustering (Managed)",
            Description = "Job-executable, managed (NetTopologySuite) spatial clustering over an inline FeatureCollection. Distinct from analytics.cluster, which runs only synchronously through the layer-scoped PostGIS SpatialAnalytics protocol and is NOT job-dispatchable; this id is the workflow/codemod-reachable counterpart. DBSCAN (eps, minPoints) or K-Means (k); every input feature is preserved one-to-one with a CLUSTER_ID attribute appended (-1 = noise for DBSCAN). Distances are evaluated in the CRS units of the supplied feature geometries — geodesic conversion is not performed.",
            Category = "analytics",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI. Non-point geometries cluster on their centroid; features with null/empty geometry are dropped before clustering.", ProcessParameterValueType.Text, required: true),
                Param("algorithm", "Algorithm", "Clustering algorithm. Allowed values: dbscan (default), kmeans.", ProcessParameterValueType.Text, defaultValue: "dbscan"),
                Param("eps", "Epsilon", "Maximum distance between neighbours for DBSCAN, in CRS units. Must be a finite positive number. Required when algorithm is dbscan.", ProcessParameterValueType.FloatingPoint),
                Param("minPoints", "Min Points", "Minimum cluster size for DBSCAN. Must be an integer >= 1. Required when algorithm is dbscan.", ProcessParameterValueType.WholeNumber),
                Param("k", "K", "Number of clusters for K-Means. Must be an integer >= 1. Required when algorithm is kmeans.", ProcessParameterValueType.WholeNumber),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "analytics.buffer-aggregate-managed",
            Title = "Buffer Aggregate (Managed)",
            Description = "Job-executable, managed (NetTopologySuite) buffer-and-dissolve over an inline FeatureCollection. Distinct from analytics.buffer-aggregate, which runs only synchronously through the layer-scoped PostGIS SpatialAnalytics protocol and is NOT job-dispatchable; this id is the workflow/codemod-reachable counterpart. Buffers every input feature by 'distance' in the supplied unit, then optionally dissolves the result into one feature per groupByFields group via CascadedPolygonUnion. Each emitted feature carries a COUNT attribute. Distance is normalised to CRS units after applying the unit factor (meters/kilometers/feet/miles) — geodesic conversion is not performed.",
            Category = "analytics",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("distance", "Distance", "Buffer distance in the supplied unit. Must be a finite non-negative number. The unit factor converts the value to meters, which are then applied as planar CRS units to the supplied geometries — only meaningful when those geometries are in a metric projected CRS.", ProcessParameterValueType.FloatingPoint, required: true),
                Param("unit", "Unit", "Distance unit. Allowed values: meters (default), kilometers, feet, miles. The chosen unit is converted to meters and applied as planar CRS units; geographic (degree) inputs are unsupported (a meters-as-degrees buffer is meaningless) — project to a metric CRS first. No geodesic conversion is performed.", ProcessParameterValueType.Text, defaultValue: "meters"),
                Param("dissolve", "Dissolve", "Dissolve buffered geometries per group (true) or emit one buffered feature per input (false). Defaults to true.", ProcessParameterValueType.Flag, defaultValue: "true"),
                Param("groupByFields", "Group By Fields", "Comma-separated attribute names used to group dissolved buffers; one feature is emitted per group. When empty, all inputs dissolve into a single feature.", ProcessParameterValueType.Text),
                Param("sourceCrs", "Source CRS", "Optional EPSG code / identifier (e.g. EPSG:3857) declaring the CRS of the input geometries. When supplied and geographic (e.g. 4326), a non-zero linear buffer is rejected because a meters-as-degrees buffer is meaningless — reproject to a projected/metric CRS first.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "analytics.density-managed",
            Title = "Density Binning (Managed)",
            Description = "Job-executable, managed (NetTopologySuite) density binning over an inline FeatureCollection. Distinct from analytics.density, which runs only synchronously through the layer-scoped PostGIS SpatialAnalytics protocol and is NOT job-dispatchable; this id is the workflow/codemod-reachable counterpart. Snaps every input geometry's point representative (centroid for non-point inputs) onto a square or pointy-top hex grid of cellSize CRS units and emits one feature per non-empty cell with COUNT and (optionally) SUM_<weightField> attributes. Geodesic conversion is not performed — cellSize is treated as CRS units.",
            Category = "analytics",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI. Features with null/empty geometry are dropped before binning.", ProcessParameterValueType.Text, required: true),
                Param("mode", "Bin Mode", "Binning mode. Allowed values: hex (default), square.", ProcessParameterValueType.Text, defaultValue: "hex"),
                Param("cellSize", "Cell Size", "Grid cell size in CRS units. Must be a finite positive number.", ProcessParameterValueType.FloatingPoint, required: true),
                Param("weightField", "Weight Field", "Optional attribute name whose numeric values are summed per cell as SUM_<weightField>. Non-numeric or missing values are skipped.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "analytics.hotspot-managed",
            Title = "Hot Spot Analysis (Getis-Ord Gi*, Managed)",
            Description = "Job-executable, managed (NetTopologySuite) Hot Spot Analysis over an inline FeatureCollection, computing the Getis-Ord Gi* statistic per feature using a fixed-distance-band conceptualization of spatial relationships. Every input feature is preserved one-to-one with GI_ZSCORE (Gi* z-score), GI_PVALUE (two-tailed p-value) and GI_BIN (Esri-style confidence bin in [-3, 3]: sign = hot/cold, magnitude = 99/95/90% significance) attributes appended. Distances are evaluated in the CRS units of the supplied feature geometries — geodesic conversion is not performed. Rejects degenerate inputs (fewer than two located features, or zero variance in the analysis field).",
            Category = "analytics",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI. Non-point geometries are analysed on their centroid; features with null/empty geometry are dropped before analysis.", ProcessParameterValueType.Text, required: true),
                Param("field", "Analysis Field", "Attribute name whose numeric values are analysed for clustering. Every located feature must carry a numeric value for this field.", ProcessParameterValueType.Text, required: true),
                Param("distanceBand", "Distance Band", "Fixed distance band in CRS units. Features within this Euclidean distance of one another are neighbours (each feature is always its own neighbour for Gi*). Must be a finite positive number.", ProcessParameterValueType.FloatingPoint, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },

        // -----------------------------------------------------------------------
        // Layer-aware overlay operations (#2206, #2139)
        // Managed (NetTopologySuite) overlay ops operating over TWO inline
        // FeatureCollections addressed by data URI — the layer-aware counterparts
        // of the single-WKB geometry.* primitives, so the high-frequency Esri
        // overlay toolset (Clip/Intersect/Union/Erase) works end-to-end and the
        // arcpy shim can promote those ops. Mirrors the managed analytics
        // executors (spatial-join/buffer-aggregate): FeatureCollection in/out, no
        // Postgres dependency, pure managed geometry.
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "overlay.clip",
            Title = "Clip (Layer-Aware)",
            Description = "Truncates every input feature's geometry to the union of the clip layer, preserving input attributes; features outside the clip region are dropped. Layer-aware counterpart of geometry.clip and the Esri Clip_analysis parity op. Also serves as the layer-level Extract (clip-by-selection) tool. Both layers are supplied inline as data:application/geo+json;base64 data URIs.",
            Category = "overlay",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("clip", "Clip Features", "Clip FeatureCollection as a data:application/geo+json;base64 data URI. Its union defines the clip region.", ProcessParameterValueType.Text, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "overlay.intersect",
            Title = "Intersect (Layer-Aware)",
            Description = "Emits the pairwise geometric intersection of every input feature with every overlapping overlay feature, carrying input attributes and merging overlay attributes (colliding overlay keys prefixed OVERLAY_). Layer-aware counterpart of geometry.intersect and the Esri Intersect_analysis parity op. Both layers are supplied inline as data:application/geo+json;base64 data URIs.",
            Category = "overlay",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("overlay", "Overlay Features", "Overlay FeatureCollection as a data:application/geo+json;base64 data URI, indexed in-memory via an STRtree.", ProcessParameterValueType.Text, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "overlay.union",
            Title = "Union (Layer-Aware)",
            Description = "Computes the planar union of the input and overlay layers, emitting input-only pieces (input geometry minus the overlay union, input attributes), overlay-only pieces (overlay geometry minus the input union, overlay attributes), and pairwise intersection pieces (merged attributes, overlay collisions prefixed OVERLAY_). Layer-aware counterpart of geometry.union and the Esri Union_analysis parity op. Both layers are supplied inline as data:application/geo+json;base64 data URIs.",
            Category = "overlay",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("overlay", "Overlay Features", "Overlay FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "overlay.erase",
            Title = "Erase (Layer-Aware)",
            Description = "Subtracts the union of the erase layer from every input feature's geometry, preserving input attributes; features fully covered by the erase layer are dropped. Layer-aware counterpart of geometry.difference and the Esri Erase_analysis parity op (the vector overlay/proximity pack's layer-level Erase). Both layers are supplied inline as data:application/geo+json;base64 data URIs.",
            Category = "overlay",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("erase", "Erase Features", "Erase FeatureCollection as a data:application/geo+json;base64 data URI. Its union is subtracted from each input geometry.", ProcessParameterValueType.Text, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "overlay.merge",
            Title = "Merge",
            Description = "Combines the input and merge FeatureCollections into a single new output, concatenating features and carrying each feature's own attributes through (Esri Merge union-schema behaviour). Use data-management.append to append into an existing target schema. Both layers are supplied inline as data:application/geo+json;base64 data URIs.",
            Category = "overlay",
            Parameters =
            [
                Param("input", "Input Features", "First FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("merge", "Merge Features", "Second FeatureCollection as a data:application/geo+json;base64 data URI to combine with the input.", ProcessParameterValueType.Text, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "overlay.split",
            Title = "Split",
            Description = "Partitions the input layer, tagging every output feature with a SPLIT_TARGET attribute. When a 'split' polygon layer is supplied, each input feature is clipped to every overlapping split-zone and tagged with that zone's splitField value (geometric partition); otherwise input features are grouped by the input 'splitField' value and tagged unchanged. Esri Split parity in a single-artifact pipeline. Layers are supplied inline as data:application/geo+json;base64 data URIs.",
            Category = "overlay",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("split", "Split Features", "Optional split-zone polygon FeatureCollection as a data:application/geo+json;base64 data URI. When supplied, input features are clipped per zone.", ProcessParameterValueType.Text),
                Param("splitField", "Split Field", "Attribute whose value names each partition: the split-zone field when a split layer is supplied, otherwise the input field to group by. Required when no split layer is supplied.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },

        // -----------------------------------------------------------------------
        // Proximity operations (#2139)
        // Managed nearest-feature distance tools. Distances are planar (CRS units);
        // geodesic conversion is not performed.
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "proximity.near",
            Title = "Near",
            Description = "Appends NEAR_FID and NEAR_DIST to every input feature describing the closest feature in the near layer (Esri Near semantics), preserving input geometry/attributes one-to-one. Distances are planar in CRS units. Inputs with no neighbour within the optional searchRadius receive NEAR_FID = -1 and NEAR_DIST = -1. Both layers are supplied inline as data:application/geo+json;base64 data URIs.",
            Category = "proximity",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("near", "Near Features", "Near (reference) FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("nearIdField", "Near ID Field", "Optional near-layer attribute used as NEAR_FID. When omitted, the near feature's 0-based ordinal is used.", ProcessParameterValueType.Text),
                Param("searchRadius", "Search Radius", "Optional maximum distance (CRS units) to consider a neighbour. Non-positive or omitted means unbounded.", ProcessParameterValueType.FloatingPoint),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "proximity.near-table",
            Title = "Generate Near Table",
            Description = "Produces a table of nearest-feature rows (IN_FID, NEAR_FID, NEAR_DIST), one per input feature that has a neighbour within the optional searchRadius (Esri GenerateNearTable semantics). Table rows are emitted as null-geometry features. Distances are planar in CRS units. Both layers are supplied inline as data:application/geo+json;base64 data URIs.",
            Category = "proximity",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("near", "Near Features", "Near (reference) FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("inputIdField", "Input ID Field", "Optional input-layer attribute used as IN_FID. When omitted, the input feature's 0-based ordinal is used.", ProcessParameterValueType.Text),
                Param("nearIdField", "Near ID Field", "Optional near-layer attribute used as NEAR_FID. When omitted, the near feature's 0-based ordinal is used.", ProcessParameterValueType.Text),
                Param("searchRadius", "Search Radius", "Optional maximum distance (CRS units) to consider a neighbour. Non-positive or omitted means unbounded.", ProcessParameterValueType.FloatingPoint),
            ],
            OutputArtifactKinds = [ArtifactKind.Table]
        },

        // -----------------------------------------------------------------------
        // Euclidean proximity operations (#2240)
        // Raster proximity products implemented natively by the heavyweight GDAL
        // worker via gdal_proximity.py. Declared RuntimeProfile = native; the lean
        // image validates these plans but never executes them.
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "proximity.euclidean-distance",
            Title = "Euclidean Distance",
            Description = "Computes a raster of the distance from each cell to the nearest source cell. Executed out-of-process by the heavyweight GDAL worker via gdal_proximity.py. Reads a base64-encoded GeoTIFF from 'source' whose non-zero (or 'values'-listed) pixels are the proximity targets; publishes the Float32 distance GeoTIFF as a data-URI artifact.",
            Category = "proximity",
            Parameters =
            [
                Param("source", "Source Raster", "Source raster as base64-encoded GeoTIFF bytes whose non-zero (or 'values'-listed) pixels are the proximity targets. Required by the native worker execution path.", ProcessParameterValueType.Text, required: true),
                Param("maxDistance", "Max Distance", "Optional maximum distance to compute. Must be > 0 when supplied. Cells beyond it take the nodata value.", ProcessParameterValueType.FloatingPoint),
                Param("distUnits", "Distance Units", "Distance units. Allowed values: GEO, PIXEL. Defaults to GEO.", ProcessParameterValueType.Text, defaultValue: "GEO"),
                Param("values", "Target Values", "Optional comma-separated list of integer source pixel values to treat as targets. When omitted, all non-zero pixels are targets.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "proximity.euclidean-allocation",
            Title = "Euclidean Allocation",
            Description = "Computes the nearest-source allocation raster (the discrete-Voronoi companion of proximity.euclidean-distance): every cell takes the VALUE/id of its nearest source cell. Executed out-of-process by the heavyweight GDAL worker via a custom worker step (gdal_euclidean_allocation.py) layered on the GDAL Python bindings plus SciPy's exact Euclidean distance transform, since stock gdal_proximity computes distance only. Reads a base64-encoded GeoTIFF from 'source' whose non-zero (or 'values'-listed) pixels are the sources; publishes the allocation GeoTIFF (source extent/cell-size/CRS/band data type preserved) as a data-URI artifact.",
            Category = "proximity",
            Parameters =
            [
                Param("source", "Source Raster", "Source raster as base64-encoded GeoTIFF bytes whose non-zero (or 'values'-listed) pixels are the allocation sources carrying the ids to assign. Required by the native worker execution path.", ProcessParameterValueType.Text, required: true),
                Param("maxDistance", "Max Distance", "Optional maximum allocation distance. Must be > 0 when supplied. Cells whose nearest source is farther take the nodata value.", ProcessParameterValueType.FloatingPoint),
                Param("distUnits", "Distance Units", "Distance units used for maxDistance. Allowed values: GEO, PIXEL. Defaults to GEO.", ProcessParameterValueType.Text, defaultValue: "GEO"),
                Param("values", "Target Values", "Optional comma-separated list of integer source pixel values to treat as allocation sources. When omitted, all non-zero pixels are sources.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },

        // -----------------------------------------------------------------------
        // Statistics/summarization operations (#2140)
        // Managed table-producing aggregates over a single inline FeatureCollection.
        // Table outputs are null-geometry FeatureCollections (one feature per row).
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "statistics.summarize",
            Title = "Summary Statistics",
            Description = "Computes per-group summary statistics over one or more caseFields (Esri Summary Statistics), emitting a table with one row per distinct case-field combination carrying the case values, a FREQUENCY count, and every requested SUM/MEAN/MIN/MAX/STDDEV aggregate. Null/non-numeric values are skipped from numeric aggregates; a null case value forms its own group. The input layer is supplied inline as a data:application/geo+json;base64 data URI.",
            Category = "statistics",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("caseFields", "Case Fields", "Comma-separated attribute columns to group by. When empty, a single all-rows summary row is produced.", ProcessParameterValueType.Text),
                Param("statistics", "Statistics", "Semicolon-separated 'field:stat' aggregates. Supported stats: count, sum, mean, min, max, stddev (sample, n-1). Example: 'pop:sum;pop:mean;pop:stddev'.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.Table]
        },
        new ProcessDefinition
        {
            ProcessId = "statistics.frequency",
            Title = "Frequency",
            Description = "Computes the count of each distinct combination of frequencyFields (Esri Frequency), emitting a table with one row per combination carrying the field values, a FREQUENCY count, and an optional SUM_<field> for each summaryFields field. A null value is its own distinct combination component. The input layer is supplied inline as a data:application/geo+json;base64 data URI.",
            Category = "statistics",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("frequencyFields", "Frequency Fields", "Comma-separated attribute columns whose distinct combinations are counted. At least one is required.", ProcessParameterValueType.Text, required: true),
                Param("summaryFields", "Summary Fields", "Optional comma-separated numeric attribute columns summed per combination as SUM_<field>.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.Table]
        },
        new ProcessDefinition
        {
            ProcessId = "statistics.calculate",
            Title = "Calculate Statistics",
            Description = "Computes descriptive statistics (COUNT, MIN, MAX, MEAN, SUM, STDDEV) for each requested field across the whole input dataset, emitting a table with one row per field keyed by a FIELD column. Null/non-numeric values are excluded; STDDEV is the sample (n-1) standard deviation and is null for fewer than two numeric values. The input layer is supplied inline as a data:application/geo+json;base64 data URI.",
            Category = "statistics",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("fields", "Fields", "Comma-separated numeric attribute columns to summarize. At least one is required.", ProcessParameterValueType.Text, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.Table]
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
                Param("cellSize", "Cell Size", "Grid cell size in meters. Must be > 0. For geographic layers the geometry is transformed to EPSG:3857 (Web Mercator) so the cell size is evaluated in meters there; those distances overstate ground distance by 1/cos(latitude) (~2x at 60°N).", ProcessParameterValueType.FloatingPoint, required: true),
                Param("weightField", "Weight Field", "Optional field name for weighted sums instead of counts.", ProcessParameterValueType.Text),
                .. SharedAnalyticsFilterParameters,
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer, ArtifactKind.Table]
        },

        // -----------------------------------------------------------------------
        // Surface-analysis operations (8)
        // DEM-derived raster products implemented natively by the heavyweight GDAL
        // worker via the gdaldem / gdal_contour / gdal_viewshed CLIs. Declared
        // RuntimeProfile = native so the
        // submit path stamps ExecutionJobSpec.RuntimeProfile = "native" and the
        // claim fence routes the job to the GDAL worker and away from the lean
        // dispatcher. The lean image still validates these plans (parameter
        // shape + per-process semantic rules) but never executes them.
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "surface.slope",
            Title = "Slope",
            Description = "Computes a slope raster from an elevation source. Executed out-of-process by the heavyweight GDAL worker via gdaldem. Reads a base64-encoded GeoTIFF DEM from 'source' and publishes the slope raster as a GeoTIFF data-URI artifact.",
            Category = "surface",
            Parameters =
            [
                .. NativeRasterSourceParameters,
                Param("units", "Units", "Slope units. Allowed values: degrees, percent. Defaults to degrees. Radians are not emitted directly by gdaldem and are rejected at submit time.", ProcessParameterValueType.Text, defaultValue: "degrees"),
                Param("zFactor", "Z Factor", "Vertical-to-horizontal scale factor. Must be > 0. Defaults to 1.0.", ProcessParameterValueType.FloatingPoint, defaultValue: "1.0"),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "surface.aspect",
            Title = "Aspect",
            Description = "Computes a compass-bearing aspect raster from an elevation source. Executed out-of-process by the heavyweight GDAL worker via gdaldem aspect. Reads a base64-encoded GeoTIFF DEM from 'source' and publishes the aspect raster as a GeoTIFF data-URI artifact.",
            Category = "surface",
            Parameters =
            [
                .. NativeRasterSourceParameters,
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "surface.hillshade",
            Title = "Hillshade",
            Description = "Computes a hillshade raster using illumination azimuth, altitude, and vertical scale. Executed out-of-process by the heavyweight GDAL worker via gdaldem hillshade. Reads a base64-encoded GeoTIFF DEM from 'source' and publishes the hillshade raster as a GeoTIFF data-URI artifact.",
            Category = "surface",
            Parameters =
            [
                .. NativeRasterSourceParameters,
                Param("azimuth", "Azimuth", "Illumination azimuth in degrees clockwise from north. Must be between 0 and 360. Defaults to 315.", ProcessParameterValueType.FloatingPoint, defaultValue: "315"),
                Param("altitude", "Altitude", "Illumination altitude above the horizon in degrees. Must be between 0 and 90. Defaults to 45.", ProcessParameterValueType.FloatingPoint, defaultValue: "45"),
                Param("zFactor", "Z Factor", "Vertical-to-horizontal scale factor. Must be > 0. Defaults to 1.0.", ProcessParameterValueType.FloatingPoint, defaultValue: "1.0"),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "surface.rugosity-tri",
            Title = "Terrain Ruggedness Index",
            Description = "Computes a terrain ruggedness index raster. Executed out-of-process by the heavyweight GDAL worker via gdaldem TRI. The current canonical implementation supports only a 3x3 neighborhood (windowRadius=1).",
            Category = "surface",
            Parameters =
            [
                .. NativeRasterSourceParameters,
                Param("windowRadius", "Window Radius", "Neighborhood radius in pixels. Must currently be 1.", ProcessParameterValueType.WholeNumber, defaultValue: "1"),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "surface.rugosity-tpi",
            Title = "Topographic Position Index",
            Description = "Computes a topographic position index raster. Executed out-of-process by the heavyweight GDAL worker via gdaldem TPI. The current canonical implementation supports only a 3x3 neighborhood (windowRadius=1).",
            Category = "surface",
            Parameters =
            [
                .. NativeRasterSourceParameters,
                Param("windowRadius", "Window Radius", "Neighborhood radius in pixels. Must currently be 1.", ProcessParameterValueType.WholeNumber, defaultValue: "1"),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "surface.roughness",
            Title = "Roughness",
            Description = "Computes a roughness raster. Executed out-of-process by the heavyweight GDAL worker via gdaldem roughness. The current canonical implementation supports only a 3x3 neighborhood (windowRadius=1).",
            Category = "surface",
            Parameters =
            [
                .. NativeRasterSourceParameters,
                Param("windowRadius", "Window Radius", "Neighborhood radius in pixels. Must currently be 1.", ProcessParameterValueType.WholeNumber, defaultValue: "1"),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "surface.contour",
            Title = "Contour",
            Description = "Generates contour lines from an elevation source. Executed out-of-process by the heavyweight GDAL worker via gdal_contour. Reads a base64-encoded GeoTIFF DEM from 'source' and publishes a GeoJSON FeatureCollection of contour lines carrying an ELEV attribute as a data-URI artifact.",
            Category = "surface",
            Parameters =
            [
                .. NativeRasterSourceParameters,
                Param("interval", "Interval", "Contour interval in the DEM's elevation units. Must be > 0. Required.", ProcessParameterValueType.FloatingPoint, required: true),
                Param("base", "Base", "Optional elevation offset relative to which intervals are interpreted.", ProcessParameterValueType.FloatingPoint),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "surface.viewshed",
            Title = "Viewshed",
            Description = "Computes a binary visibility raster from a DEM and an observer location. Executed out-of-process by the heavyweight GDAL worker via gdal_viewshed. Reads a base64-encoded GeoTIFF DEM from 'source'; publishes the visibility GeoTIFF as a data-URI artifact. The observer is placed at (observerX, observerY) in the DEM's georeferenced units.",
            Category = "surface",
            Parameters =
            [
                .. NativeRasterSourceParameters,
                Param("observerX", "Observer X", "Observer X coordinate in the DEM's georeferenced units. Required.", ProcessParameterValueType.FloatingPoint, required: true),
                Param("observerY", "Observer Y", "Observer Y coordinate in the DEM's georeferenced units. Required.", ProcessParameterValueType.FloatingPoint, required: true),
                Param("observerHeight", "Observer Height", "Observer height above the surface. Must be >= 0. Defaults to 2.", ProcessParameterValueType.FloatingPoint, defaultValue: "2"),
                Param("targetHeight", "Target Height", "Target height above the surface. Must be >= 0. Defaults to 0.", ProcessParameterValueType.FloatingPoint, defaultValue: "0"),
                Param("maxDistance", "Max Distance", "Optional maximum visibility distance in georeferenced units. Must be > 0 when supplied.", ProcessParameterValueType.FloatingPoint),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },

        // -----------------------------------------------------------------------
        // Raster operations (12)
        // Raster analysis and mutation workflows implemented natively by the
        // heavyweight GDAL worker via the gdalwarp / gdalinfo CLI tools. Declared
        // RuntimeProfile = native so the submit path stamps the spec native and
        // the claim fence routes the job to the GDAL worker and away from the
        // lean dispatcher. The lean image validates these plans (parameter shape
        // + per-process semantic rules) but never executes them.
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "raster.clip",
            Title = "Clip Raster",
            Description = "Clips a raster to the supplied boundary geometry. Executed out-of-process by the heavyweight GDAL worker via gdalwarp -cutline. Reads a base64-encoded GeoTIFF from 'source' and the boundary WKB; publishes the clipped raster as a GeoTIFF data-URI artifact.",
            Category = "raster",
            Parameters =
            [
                .. NativeRasterSourceParameters,
                Param("boundary", "Boundary", "Clip boundary geometry in WKB format.", ProcessParameterValueType.Wkb, required: true),
                Param("boundarySrid", "Boundary SRID", "Spatial reference identifier of the boundary geometry when it differs from the raster SRID.", ProcessParameterValueType.Srid),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "raster.reproject",
            Title = "Reproject Raster",
            Description = "Reprojects a raster into a new spatial reference using the requested resampling algorithm. Executed out-of-process by the heavyweight GDAL worker via gdalwarp -t_srs. Reads a base64-encoded GeoTIFF from 'source'; publishes the reprojected GeoTIFF as a data-URI artifact.",
            Category = "raster",
            Parameters =
            [
                .. NativeRasterSourceParameters,
                Param("targetSrid", "Target SRID", "Target spatial reference identifier.", ProcessParameterValueType.Srid, required: true),
                Param("resampling", "Resampling", "Resampling algorithm. Allowed values: nearestneighbor, bilinear, cubic, lanczos. Defaults to bilinear.", ProcessParameterValueType.Text, defaultValue: "bilinear"),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "raster.statistics",
            Title = "Raster Statistics",
            Description = "Computes per-band statistics for a raster. Band selection is optional and uses a comma-separated list. Executed out-of-process by the heavyweight GDAL worker via gdalinfo -stats. Publishes a JSON scalar artifact (min/max/mean/stddev per band).",
            Category = "raster",
            Parameters =
            [
                .. NativeRasterSourceParameters,
                Param("bands", "Bands", "Optional comma-separated 1-based band numbers to analyze. When omitted, all bands are analyzed.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.Scalar],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "raster.histogram",
            Title = "Raster Histogram",
            Description = "Computes per-band histograms for a raster. Executed out-of-process by the heavyweight GDAL worker via gdalinfo -hist. Publishes a JSON scalar artifact (256 bin counts per band, fixed by gdalinfo).",
            Category = "raster",
            Parameters =
            [
                .. NativeRasterSourceParameters,
                Param("bands", "Bands", "Optional comma-separated 1-based band numbers to analyze. When omitted, all bands are analyzed.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.Scalar],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "raster.zonal-statistics",
            Title = "Zonal Statistics",
            Description = "Computes zonal aggregates by intersecting a raster with polygonal zones. Executed out-of-process by the heavyweight GDAL worker via per-zone gdalwarp clip + gdalinfo. Reads 'source' (raster) and 'zones' (inline GeoJSON FeatureCollection of zone polygons); zonesLayerId-driven sourcing is deferred to a follow-on.",
            Category = "raster",
            Parameters =
            [
                .. NativeRasterSourceParameters,
                Param("zones", "Zones Inline", "Inline zone polygons as a base64-encoded GeoJSON FeatureCollection. Required by the native worker execution path; zonesLayerId-resolved sourcing is a follow-on.", ProcessParameterValueType.Text, required: true),
                // Reserved placeholder: the executor reads only the inline `zones`, so there is
                // no layer access to gate (honua-server#3046 review). Give this a real
                // ProcessLayerAccess when the resolution path lands.
                Param("zonesLayerId", "Zones Layer", "Layer identifier whose feature geometries define the aggregation zones. Optional today; reserved for submit-time zones-layer-to-zones resolution (a follow-on).", ProcessParameterValueType.LayerId, layerAccess: ProcessLayerAccess.None),
                Param("band", "Band", "1-based raster band to aggregate. Defaults to 1.", ProcessParameterValueType.WholeNumber, defaultValue: "1"),
                Param("statistics", "Statistics", "Comma-separated stat names. Allowed values: count, sum, mean, min, max, stddev, variance.", ProcessParameterValueType.Text, defaultValue: "count,mean,stddev,min,max,sum"),
            ],
            OutputArtifactKinds = [ArtifactKind.Table],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "raster.resample",
            Title = "Resample Raster",
            Description = "Changes a raster's cell size using the requested resampling algorithm. Executed out-of-process by the heavyweight GDAL worker via gdalwarp -tr. Reads a base64-encoded GeoTIFF from 'source'; publishes the resampled GeoTIFF as a data-URI artifact.",
            Category = "raster",
            Parameters =
            [
                .. NativeRasterSourceParameters,
                Param("cellSize", "Cell Size", "Target pixel size in the raster's georeferenced units. Must be > 0. Applied to both axes unless 'cellSizeY' is supplied.", ProcessParameterValueType.FloatingPoint, required: true),
                Param("cellSizeY", "Cell Size Y", "Optional distinct vertical pixel size in georeferenced units. Must be > 0. Defaults to 'cellSize' (square pixels).", ProcessParameterValueType.FloatingPoint),
                Param("resampling", "Resampling", "Resampling algorithm. Allowed values: nearestneighbor, bilinear, cubic, lanczos. Defaults to bilinear.", ProcessParameterValueType.Text, defaultValue: "bilinear"),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "raster.interpolate-idw",
            Title = "Interpolate (IDW)",
            Description = "Interpolates a continuous raster surface from scattered points using inverse-distance weighting. Executed out-of-process by the heavyweight GDAL worker via gdal_grid -a invdist. Reads a base64-encoded GeoJSON point FeatureCollection from 'points'; publishes the interpolated GeoTIFF as a data-URI artifact.",
            Category = "raster",
            Parameters =
            [
                Param("points", "Points", "Source points as a base64-encoded GeoJSON FeatureCollection. Required by the native worker execution path.", ProcessParameterValueType.Text, required: true),
                Param("zField", "Z Field", "Attribute name holding the value to interpolate. When omitted, gdal_grid uses the geometry Z coordinate.", ProcessParameterValueType.Text),
                Param("power", "Power", "Inverse-distance weighting exponent. Must be > 0. Defaults to 2.0.", ProcessParameterValueType.FloatingPoint, defaultValue: "2.0"),
                Param("smoothing", "Smoothing", "Smoothing parameter applied during interpolation. Must be >= 0. Defaults to 0.", ProcessParameterValueType.FloatingPoint, defaultValue: "0"),
                Param("radius", "Search Radius", "Optional search radius in georeferenced units. Must be > 0 when supplied. When omitted, all points contribute (global search).", ProcessParameterValueType.FloatingPoint),
                Param("width", "Width", "Optional output raster width in pixels. Must be > 0 and supplied together with 'height'.", ProcessParameterValueType.WholeNumber),
                Param("height", "Height", "Optional output raster height in pixels. Must be > 0 and supplied together with 'width'.", ProcessParameterValueType.WholeNumber),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "raster.interpolate-kriging",
            Title = "Interpolate (Kriging)",
            Description = "FLAGGED / UNSUPPORTED in this build: kriging interpolation requires a kriging-capable numerical backend that the worker image does not bundle (stock GDAL gdal_grid has no kriging algorithm). The process is advertised so callers can discover the limitation; a submitted job FAILS with a clear message rather than silently substituting a different algorithm. Use raster.interpolate-idw for inverse-distance-weighted interpolation.",
            Category = "raster",
            Parameters =
            [
                Param("points", "Points", "Source points as a base64-encoded GeoJSON FeatureCollection.", ProcessParameterValueType.Text, required: true),
                Param("zField", "Z Field", "Attribute name holding the value to interpolate.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "raster.mosaic",
            Title = "Mosaic Rasters",
            Description = "Combines two or more input rasters into a single raster. Executed out-of-process by the heavyweight GDAL worker via gdalwarp. Reads a '|'-separated list of base64-encoded GeoTIFFs from 'sources'; publishes the mosaicked GeoTIFF as a data-URI artifact. The 'operator' selects overlap behavior (first/last); statistical operators (min/max/mean/sum) are not yet available on the native worker.",
            Category = "raster",
            Parameters =
            [
                Param("sources", "Sources", "Two or more source rasters as base64-encoded GeoTIFFs separated by '|'. Required by the native worker execution path.", ProcessParameterValueType.Text, required: true),
                Param("operator", "Operator", "Overlap behavior. Allowed values: first, last. Defaults to last (later-listed source wins).", ProcessParameterValueType.Text, defaultValue: "last"),
                Param("resampling", "Resampling", "Resampling algorithm. Allowed values: nearestneighbor, bilinear, cubic, lanczos. Defaults to nearestneighbor.", ProcessParameterValueType.Text, defaultValue: "nearestneighbor"),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "raster.map-algebra",
            Title = "Map Algebra",
            Description = "Evaluates an allow-listed arithmetic/logical expression across one or more input rasters. Executed out-of-process by the heavyweight GDAL worker via gdal_calc.py. Reads a '|'-separated list of base64-encoded GeoTIFFs from 'sources' (bound to band variables A, B, C, …) and a validated 'expression'; publishes the result GeoTIFF as a data-URI artifact. The expression is restricted to single-letter band variables, numeric literals, a fixed operator set, and a small allow-list of NumPy functions; arbitrary input is never shell- or eval-evaluated.",
            Category = "raster",
            Parameters =
            [
                Param("sources", "Sources", "One or more source rasters as base64-encoded GeoTIFFs separated by '|', bound to band variables A, B, C, … in order. Required by the native worker execution path.", ProcessParameterValueType.Text, required: true),
                Param("expression", "Expression", "Allow-listed map-algebra expression over the band variables (e.g. '(A-B)/(A+B)'). Required.", ProcessParameterValueType.Text, required: true),
                Param("dataType", "Output Type", "Optional GDAL output data type. Allowed values: Byte, Int16, UInt16, Int32, UInt32, Float32, Float64.", ProcessParameterValueType.Text),
                Param("noData", "NoData Value", "Optional output NoData value tagged on the result band and used to fill masked cells. When omitted, the first source raster's band NoData is detected and propagated. Each input is masked by its own NoData by default.", ProcessParameterValueType.FloatingPoint),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "raster.spectral-index",
            Title = "Spectral Index",
            Description = "Computes a named spectral index from band-role rasters, compiling down to the gdal_calc.py map-algebra core. Executed out-of-process by the heavyweight GDAL worker. Each required band role is supplied as its own base64-encoded GeoTIFF; publishes the Float32 index GeoTIFF as a data-URI artifact. Supported indices: NDVI (nir, red), NDWI (green, nir), NDBI (swir, nir), SAVI (nir, red), EVI (nir, red, blue).",
            Category = "raster",
            Parameters =
            [
                Param("index", "Index", "Spectral index preset. Allowed values: NDVI, NDWI, NDBI, SAVI, EVI.", ProcessParameterValueType.Text, required: true,
                    allowedValues: ["NDVI", "NDWI", "NDBI", "SAVI", "EVI"]),
                Param("red", "Red Band", "Red-band raster as a base64-encoded GeoTIFF. Required by NDVI, SAVI, EVI.", ProcessParameterValueType.Text),
                Param("nir", "NIR Band", "Near-infrared-band raster as a base64-encoded GeoTIFF. Required by NDVI, NDWI, NDBI, SAVI, EVI.", ProcessParameterValueType.Text),
                Param("green", "Green Band", "Green-band raster as a base64-encoded GeoTIFF. Required by NDWI.", ProcessParameterValueType.Text),
                Param("swir", "SWIR Band", "Short-wave-infrared-band raster as a base64-encoded GeoTIFF. Required by NDBI.", ProcessParameterValueType.Text),
                Param("blue", "Blue Band", "Blue-band raster as a base64-encoded GeoTIFF. Required by EVI.", ProcessParameterValueType.Text),
                Param("L", "Soil Factor (SAVI)", "SAVI soil-adjustment factor in the closed range [0, 1]. Defaults to 0.5. Ignored by other indices.", ProcessParameterValueType.FloatingPoint, defaultValue: "0.5"),
                Param("noData", "NoData Value", "Optional output NoData value tagged on the index band and used to fill masked cells. When omitted, the first band-role raster's band NoData is detected and propagated.", ProcessParameterValueType.FloatingPoint),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "raster.reclassify",
            Title = "Reclassify",
            Description = "Remaps source pixel values per a supplied remap table. Executed out-of-process by the heavyweight GDAL worker via gdal_calc.py. Reads a base64-encoded GeoTIFF from 'source' and a 'remap' table; publishes the reclassified GeoTIFF as a data-URI artifact. Remap entries are ';'-separated 'key:value' pairs where key is a single number or a half-open range 'lo..hi' (lower-inclusive, upper-exclusive); unmatched pixels take 'defaultValue' or the original value.",
            Category = "raster",
            Parameters =
            [
                .. NativeRasterSourceParameters,
                Param("remap", "Remap Table", "Reclassification table: ';'-separated 'value:newValue' or 'lo..hi:newValue' entries (e.g. '0..10:1;10..20:2'). Required.", ProcessParameterValueType.Text, required: true),
                Param("defaultValue", "Default Value", "Optional output value for pixels matching no remap entry. When omitted, unmatched pixels keep their original value.", ProcessParameterValueType.FloatingPoint),
                Param("dataType", "Output Type", "Optional GDAL output data type. Allowed values: Byte, Int16, UInt16, Int32, UInt32, Float32, Float64.", ProcessParameterValueType.Text),
                Param("noData", "NoData Value", "Optional output NoData value tagged on the result band and used to fill masked cells. When omitted, the source raster's band NoData is detected and propagated.", ProcessParameterValueType.FloatingPoint),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },

        // -----------------------------------------------------------------------
        // Imagery / ML inference (1)
        // Delegated cloud inference (#2241): Honua GP orchestrates, a configured
        // cloud endpoint runs the model. No model runtime is bundled and no
        // training/distributed inference exists here by design. Managed profile:
        // the lean dispatcher executes the delegation (it is an HTTP exchange),
        // and when no backend is configured the job FAILS with a clear
        // unavailability message (the raster.interpolate-kriging honesty posture).
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "imagery.classify",
            Title = "Classify Imagery (Delegated Inference)",
            Description = "Runs supervised/unsupervised classification, segmentation, or object detection on a raster scene by DELEGATING inference to a configured cloud backend (Geoprocessing:ImageryInference — generic 'http' adapter speaking Honua's own JSON inference contract, which a hosted model server or a thin gateway in front of one implements; it is NOT the OpenAI chat-completions wire format; 'sagemaker'/'vertex'/'azureml' SDK adapters are recognized but not yet supported and fail clearly). The model is a reference into the backend, never a Honua-implemented algorithm. Reads a base64-encoded GeoTIFF from 'source' (or a layerId/rasterId resolved at submit time) and publishes either a classification/segmentation GeoTIFF (georeferencing preserved byte-for-byte from the backend and verified against the source extent/CRS) or detected features as a GeoJSON FeatureCollection in WGS 84 (EPSG:4326) longitude/latitude per RFC 7946. The source must itself be an axis-aligned north-up GeoTIFF with an EPSG-coded CRS (user-defined GeoKey 32767 definitions are rejected) so the output location can be verified; detections are additionally checked against the source footprint where the CRS can be mapped to WGS 84 in-process (WGS 84, Web Mercator, and UTM zones). When no backend is configured the process is advertised but every execution fails with a clear 'no cloud inference backend is configured' message — no silent stub, no fake result.",
            Category = "imagery",
            Parameters =
            [
                .. NativeRasterSourceParameters,
                Param("model", "Model Reference", "Model reference passed verbatim to the configured inference backend (for example a deployed model or endpoint-local model name). Optional only when Geoprocessing:ImageryInference:DefaultModel is configured; otherwise the job fails at execution with a clear message.", ProcessParameterValueType.Text),
                Param("task", "Task", "Inference task. Allowed values: classification, segmentation, detection. Defaults to classification.", ProcessParameterValueType.Text, defaultValue: "classification",
                    allowedValues: ["classification", "segmentation", "detection"]),
                Param("confidenceThreshold", "Confidence Threshold", "Optional minimum score in the closed range [0, 1] forwarded to the backend for detection/segmentation filtering.", ProcessParameterValueType.FloatingPoint),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster, ArtifactKind.FeatureLayer],
            // The backend decides which shape a scene yields, so exactly ONE of
            // these is produced per run — they are alternatives, not a pair.
            OutputsAreAlternatives = true
        },

        // -----------------------------------------------------------------------
        // Conversion operations (6)
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
                Param("target", "Target Format", "Target geometry encoding. Allowed values: wkt, geojson, wkb, ewkt.", ProcessParameterValueType.Text, required: true,
                    allowedValues: ["wkt", "geojson", "wkb", "ewkt"]),
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
            Description = "Exports a raster into another raster format such as GTiff, PNG, JPEG, or COG. Executes the real GDAL gdal_translate path in the native worker (#2138).",
            Category = "conversion",
            Parameters =
            [
                .. NativeRasterSourceParameters,
                Param("targetFormat", "Target Format", "Target raster format. Allowed values: GTiff, PNG, JPEG, COG.", ProcessParameterValueType.Text, required: true),
                Param("compression", "Compression", "Optional format-specific compression hint passed as gdal_translate -co COMPRESS=<value>.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "conversion.raster-reproject",
            Title = "Raster CRS Conversion",
            Description = "Exports a raster into another spatial reference as an explicit conversion workflow. Executes the real GDAL gdalwarp path in the native worker (#2138).",
            Category = "conversion",
            Parameters =
            [
                .. NativeRasterSourceParameters,
                Param("targetSrid", "Target SRID", "Target spatial reference identifier.", ProcessParameterValueType.Srid, required: true),
                Param("resampling", "Resampling", "Resampling algorithm. Allowed values: nearestneighbor, bilinear, cubic, lanczos. Defaults to bilinear.", ProcessParameterValueType.Text, defaultValue: "bilinear"),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "conversion.polygonize",
            Title = "Polygonize Raster",
            Description = "Vectorizes a raster into polygon features, one per connected region of equal pixel value. Executed out-of-process by the heavyweight GDAL worker via gdal_polygonize.py. Reads a base64-encoded GeoTIFF from 'source'; publishes a GeoJSON FeatureCollection carrying the pixel value in the 'fieldName' attribute as a data-URI artifact.",
            Category = "conversion",
            Parameters =
            [
                .. NativeRasterSourceParameters,
                Param("band", "Band", "Source band to vectorize. Must be a positive integer. Defaults to 1.", ProcessParameterValueType.WholeNumber, defaultValue: "1"),
                Param("connectedness", "Connectedness", "Pixel connectedness. Allowed values: 4, 8. Defaults to 4.", ProcessParameterValueType.Text, defaultValue: "4"),
                Param("fieldName", "Field Name", "Output attribute holding the pixel value. Must match ^[A-Za-z_][A-Za-z0-9_]*$. Defaults to DN.", ProcessParameterValueType.Text, defaultValue: "DN"),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "conversion.rasterize",
            Title = "Rasterize Features",
            Description = "Burns vector features into a new raster grid. Executed out-of-process by the heavyweight GDAL worker via gdal_rasterize. Reads a base64-encoded GeoJSON FeatureCollection from 'source' and burns either a fixed 'burnValue' or a numeric 'attribute'; publishes the GeoTIFF as a data-URI artifact. The output grid is defined by 'cellSize' (resolution) or 'width'+'height' (pixels); the extent is taken from the input layer.",
            Category = "conversion",
            Parameters =
            [
                Param("source", "Source Features", "Source features as a base64-encoded GeoJSON FeatureCollection. Required by the native worker execution path.", ProcessParameterValueType.Text, required: true),
                Param("burnValue", "Burn Value", "Fixed value to burn into the raster. Supply exactly one of 'burnValue' or 'attribute'.", ProcessParameterValueType.FloatingPoint),
                Param("attribute", "Attribute", "Numeric source attribute whose value is burned per feature. Must match ^[A-Za-z_][A-Za-z0-9_]*$. Supply exactly one of 'burnValue' or 'attribute'.", ProcessParameterValueType.Text),
                Param("cellSize", "Cell Size", "Output pixel size in the source CRS units. Must be > 0. Supply either 'cellSize' or 'width'+'height'.", ProcessParameterValueType.FloatingPoint),
                Param("width", "Width", "Output raster width in pixels. Must be > 0 and supplied together with 'height'.", ProcessParameterValueType.WholeNumber),
                Param("height", "Height", "Output raster height in pixels. Must be > 0 and supplied together with 'width'.", ProcessParameterValueType.WholeNumber),
                Param("nodata", "NoData", "Optional output nodata value.", ProcessParameterValueType.FloatingPoint),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
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
            ExecutionTier = ProcessExecutionTier.Mutating,
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
            ProcessId = "data-management.append",
            Title = "Append",
            Description = "Appends a source FeatureCollection into the schema of a target FeatureCollection (Esri Append). The target features are preserved and source features are projected onto the target's field set; an optional fieldMap remaps source field names. Both layers are supplied inline as data:application/geo+json;base64 data URIs. Use overlay.merge to produce a new union-schema output instead. Pure managed — no GDAL/GEOS dependency.",
            Category = "data-management",
            Parameters =
            [
                Param("input", "Target Features", "Target FeatureCollection as a data:application/geo+json;base64 data URI. Its features are preserved and define the output schema.", ProcessParameterValueType.Text, required: true),
                Param("append", "Source Features", "Source FeatureCollection as a data:application/geo+json;base64 data URI to append into the target schema.", ProcessParameterValueType.Text, required: true),
                Param("fieldMap", "Field Map", "Optional semicolon-separated 'source:target' field name pairs used to remap source attributes onto target fields.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "data-management.delete-features",
            Title = "Delete Features",
            Description = "Deletes features matching a filter from a source layer. Destructive — requires approval. At least one of 'where' or 'objectIds' must be supplied to prevent unbounded deletion.",
            Category = "data-management",
            ExecutionTier = ProcessExecutionTier.Mutating,
            Parameters =
            [
                // Destructive target: gated on the layer DELETE grant, not a generic write
                // (honua-server#3046 review).
                Param("layerId", "Layer", "Target layer identifier.", ProcessParameterValueType.LayerId, required: true, layerAccess: ProcessLayerAccess.Delete),
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
            ExecutionTier = ProcessExecutionTier.Mutating,
            Parameters =
            [
                // Mutating target: gated on the layer UPDATE grant (honua-server#3046 review).
                Param("layerId", "Layer", "Target layer identifier.", ProcessParameterValueType.LayerId, required: true, layerAccess: ProcessLayerAccess.Update),
                Param("fieldName", "Field Name", "Simple identifier naming the field to update (letters, digits, underscore; no dotted paths).", ProcessParameterValueType.Text, required: true),
                Param("expression", "Expression", "Constant or SQL expression evaluated per feature. Parsed by the same allow-listed expression gate FeatureServer.Edits.CalculateFieldValue uses.", ProcessParameterValueType.Text, required: true),
                Param("where", "Where", "Optional ArcGIS SQL filter selecting features to update.", ProcessParameterValueType.Text),
                Param("objectIds", "Object IDs", "Optional comma-separated feature identifiers to update.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.Scalar]
        },

        // -----------------------------------------------------------------------
        // Data-import operations (1)
        // First-class durable import pipeline (#1630). import.dataset owns the
        // full fetch -> validate+chunk -> import -> flatten -> tile ->
        // extent+MVT refresh -> provenance flow as ONE managed durable job,
        // composing the existing import / publishing / raster services. This is
        // the canonical execution layer the geospatial-mcp publish_data family
        // submits into. Managed profile: the steps are orchestration + provider
        // service calls, not native GDAL work, so the lean dispatcher runs it.
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "import.dataset",
            Title = "Import Dataset",
            Description = "Imports a staged geospatial dataset end-to-end as one durable job: stages the source, imports it (the streaming importer enforces the per-feature geometry size guard / chunking), flattens the generic imported table into a typed layer (rebuilding the MVT materialization), optionally tiles a raster layer with overviews, refreshes the layer and service extents, and records a provenance artifact. Resumes by re-running from the staged source under overwrite idempotency.",
            Category = "import",
            ExecutionTier = ProcessExecutionTier.Mutating,
            Parameters =
            [
                Param("connection", "Connection", "Registered secure connection id (GUID) or name identifying the target Honua catalog database.", ProcessParameterValueType.Text, required: true),
                Param("sourcePath", "Source Path", "Absolute path to the staged source file on the worker-accessible filesystem.", ProcessParameterValueType.Text, required: true),
                Param("fileName", "File Name", "Original file name, used for format detection (e.g. parcels.geojson).", ProcessParameterValueType.Text, required: true),
                Param("tableName", "Table Name", "Target table name for the generic imported table (imported_<table>).", ProcessParameterValueType.Text, required: true),
                Param("layerName", "Layer Name", "Display name for the published typed layer.", ProcessParameterValueType.Text, required: true),
                Param("serviceName", "Service Name", "Service to publish the layer into. Defaults to 'default'.", ProcessParameterValueType.Text),
                Param("description", "Description", "Optional layer description.", ProcessParameterValueType.Text),
                Param("geometryColumn", "Geometry Column", "Optional geometry column name override for the typed layer.", ProcessParameterValueType.Text),
                Param("targetSchema", "Target Schema", "Optional schema for the imported/typed data. Defaults to the configured operational-data schema (honua_data).", ProcessParameterValueType.Text),
                Param("sourceUrl", "Source URL", "Optional originating URL when the source was fetched from a remote object, recorded in provenance.", ProcessParameterValueType.Text),
                Param("sourceSrid", "Source SRID", "Optional source spatial reference identifier when the source lacks embedded CRS metadata.", ProcessParameterValueType.Srid),
                Param("targetSrid", "Target SRID", "Target spatial reference identifier for the imported geometries. Defaults to 4326.", ProcessParameterValueType.Srid),
                // Destination, never a source: the importer tiles INTO this layer and never
                // reads it, so the submit gate must not demand a read grant on it
                // (honua-server#3046 review).
                Param("rasterLayerId", "Raster Layer", "When the source is a raster, the layer identifier to tile into; triggers raster import, statistics, and tile/overview pre-generation.", ProcessParameterValueType.LayerId, layerAccess: ProcessLayerAccess.Insert),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },

        // -----------------------------------------------------------------------
        // GeoETL transform operations (8)
        // Reconciled from feat/geoetl-baseline onto the #1185 add-a-capability
        // contract: each transform reads a FeatureCollection data URI on the
        // canonical 'input' parameter and publishes a FeatureCollection data URI,
        // carrying feature attributes through so they compose as workflow nodes.
        // Managed NetTopologySuite only — no GDAL.
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "transform.attribute-rename",
            Title = "Rename Attribute",
            Description = "Renames one feature attribute key to another across every feature in the input FeatureCollection, preserving the value and geometry. Features lacking the source attribute pass through unchanged.",
            Category = "transform",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("from", "From", "Existing attribute name to rename.", ProcessParameterValueType.Text, required: true),
                Param("to", "To", "New attribute name.", ProcessParameterValueType.Text, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "transform.attribute-cast",
            Title = "Cast Attribute",
            Description = "Coerces one attribute to a target CLR type. Supported types: int, long, double, bool, string. Uncoercible rows are handled per the onError policy (drop/null/keep).",
            Category = "transform",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("field", "Field", "Attribute name to cast.", ProcessParameterValueType.Text, required: true),
                Param("to", "Target Type", "Target CLR type. Allowed values: int, long, double, bool, string.", ProcessParameterValueType.Text, required: true),
                Param("onError", "On Error", "Behavior for uncoercible rows. Allowed values: drop (default), null, keep.", ProcessParameterValueType.Text, defaultValue: "drop"),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "transform.computed-field",
            Title = "Computed Field",
            Description = "Adds a new attribute derived from existing attributes. Two AOT-safe modes (no reflection, no runtime code generation): the legacy fixed op set (concat, add, subtract, multiply, divide, const), and a sandboxed expression engine (op=expression, or simply supply 'expression') that evaluates a whitelisted, parsed AST — arithmetic, string (concat/substr/upper/lower/trim/replace/length), conditional (if/coalesce/ternary ?:), comparison/logical, math (abs/round/floor/ceil/sqrt/pow/min/max), date (now/year/month/day/parsedate), cast/number/string, and field references. Example: upper(trim(name)) + \"-\" + cast(year, string). Rows whose arithmetic/expression evaluation fails (e.g. a non-numeric operand) are dropped as row-level data errors.",
            Category = "transform",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("target", "Target Field", "Attribute name to write the computed value to.", ProcessParameterValueType.Text, required: true),
                Param("op", "Operation", "Computation. Allowed values: concat, add, subtract, multiply, divide, const, expression. Optional when 'expression' is supplied (defaults to expression mode).", ProcessParameterValueType.Text),
                Param("expression", "Expression", "Whitelisted expression evaluated per feature when op=expression. Bare identifiers reference source attributes; supports arithmetic, string/conditional/math/date functions, comparison and logical operators, and a ternary. AOT-safe parsed AST — no reflection, no arbitrary code. Example: upper(trim(name)) + \"-\" + cast(year, string).", ProcessParameterValueType.Text),
                Param("fields", "Fields", "Comma-separated source field names for the concat op.", ProcessParameterValueType.Text),
                Param("separator", "Separator", "Join separator for the concat op.", ProcessParameterValueType.Text),
                Param("left", "Left Operand", "Left operand for arithmetic ops: a source field name, or a numeric literal prefixed with '='.", ProcessParameterValueType.Text),
                Param("right", "Right Operand", "Right operand for arithmetic ops: a source field name, or a numeric literal prefixed with '='.", ProcessParameterValueType.Text),
                Param("value", "Constant Value", "Literal value assigned to the target for the const op.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "transform.attribute-filter",
            Title = "Attribute Filter",
            Description = "Passes through only features whose attribute satisfies a simple comparison, dropping the rest. Supported ops: eq, neq, gt, gte, lt, lte, contains, exists. Numeric operators parse both operands as doubles; string operators compare ordinally.",
            Category = "transform",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("field", "Field", "Attribute name to test.", ProcessParameterValueType.Text, required: true),
                Param("op", "Operator", "Comparison operator. Allowed values: eq, neq, gt, gte, lt, lte, contains, exists. Defaults to eq.", ProcessParameterValueType.Text, defaultValue: "eq"),
                Param("value", "Value", "Comparison operand. Omitted for the 'exists' op.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "transform.attribute-join",
            Title = "Attribute Join",
            Description = "Joins the input FeatureCollection to a second 'right' FeatureCollection (a second DAG input, a Honua layer materialized to GeoJSON, or a lookup table) on key columns, bringing selected right-side fields onto each output feature. Inner or left join. Managed in-memory hash join: the RIGHT (build) side is fully materialized — bounded by the configured MaxArtifactBytes ceiling (no spill); the LEFT side streams. When an input row matches multiple right rows the join fans out one output per match.",
            Category = "transform",
            Parameters =
            [
                Param("input", "Input Features", "Input (left/probe) FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("right", "Right Features", "Join (right/build) FeatureCollection as a data:application/geo+json;base64 data URI. Materialized into an in-memory hash table keyed by rightKeys.", ProcessParameterValueType.Text, required: true),
                Param("leftKeys", "Left Keys", "Comma-separated input attribute names forming the join key.", ProcessParameterValueType.Text, required: true),
                Param("rightKeys", "Right Keys", "Comma-separated right attribute names forming the join key. Defaults to leftKeys. Must match the leftKeys column count.", ProcessParameterValueType.Text),
                Param("fields", "Carry Fields", "Comma-separated right-side fields to bring onto the output. When omitted, all right attributes are carried.", ProcessParameterValueType.Text),
                Param("type", "Join Type", "Join type. Allowed values: inner (default), left. Left preserves unmatched input features with null carried fields.", ProcessParameterValueType.Text, defaultValue: "inner"),
                Param("prefix", "Field Prefix", "Optional prefix prepended to every carried right-side field name to avoid collisions with input attributes.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "transform.aggregate",
            Title = "Aggregate",
            Description = "Group-by aggregate over the input FeatureCollection. Groups by zero or more attributes (groupBy) and emits one feature per group carrying the group-key attributes plus aggregate columns. Aggregate functions (semicolon-separated 'field:function[:alias]'): count, sum, min, max, mean, stddev, first, collect. An optional geometry aggregate (union/centroid/extent) reduces each group's geometries to a single representative geometry. Managed NetTopologySuite — running scalar accumulators bound memory to the grouped output.",
            Category = "transform",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("groupBy", "Group By", "Comma-separated attribute names to group by. When empty the whole stream collapses into a single group.", ProcessParameterValueType.Text),
                Param("aggregates", "Aggregates", "Semicolon-separated 'field:function[:alias]' aggregate specs. Functions: count, sum, min, max, mean, stddev, first, collect. When omitted a plain group COUNT is emitted. Example: 'pop:sum;pop:mean;name:collect'.", ProcessParameterValueType.Text),
                Param("geometry", "Geometry Aggregate", "Optional per-group geometry reduction. Allowed values: none (default), union, centroid, extent.", ProcessParameterValueType.Text, defaultValue: "none"),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "transform.pivot",
            Title = "Pivot",
            Description = "Reshapes a long (tall) FeatureCollection into a wide one: rows sharing a groupBy key collapse into one feature, and the distinct values of the pivotField column become new attribute columns taking their value from valueField. Last-write-wins on cell collisions. The first row's geometry per group is carried. Managed — no native dependency.",
            Category = "transform",
            Parameters =
            [
                Param("input", "Input Features", "Input (long) FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("groupBy", "Group By", "Comma-separated attribute names identifying each output row. When empty all input rows pivot into a single feature.", ProcessParameterValueType.Text),
                Param("pivotField", "Pivot Field", "Attribute whose distinct values become new output columns.", ProcessParameterValueType.Text, required: true),
                Param("valueField", "Value Field", "Attribute whose value fills each pivoted cell.", ProcessParameterValueType.Text, required: true),
                Param("prefix", "Column Prefix", "Optional prefix prepended to each pivoted column name.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "transform.unpivot",
            Title = "Unpivot",
            Description = "Melts a wide FeatureCollection into a long one: for each input feature and each column in 'fields', emits one output feature carrying the 'keep' columns plus a nameField (source column name) and valueField (its value), reusing the input geometry. The inverse of transform.pivot. Managed — no native dependency.",
            Category = "transform",
            Parameters =
            [
                Param("input", "Input Features", "Input (wide) FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("fields", "Fields", "Comma-separated attribute columns to unpivot; one output feature is emitted per column per input feature.", ProcessParameterValueType.Text, required: true),
                Param("keep", "Keep Fields", "Comma-separated attribute columns carried unchanged onto every output feature.", ProcessParameterValueType.Text),
                Param("nameField", "Name Field", "Output column receiving the source column name. Defaults to 'name'.", ProcessParameterValueType.Text, defaultValue: "name"),
                Param("valueField", "Value Field", "Output column receiving the source column value. Defaults to 'value'.", ProcessParameterValueType.Text, defaultValue: "value"),
                Param("dropNulls", "Drop Nulls", "Skip output rows whose unpivoted value is null. Defaults to false.", ProcessParameterValueType.Flag, defaultValue: "false"),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "transform.spatial-filter",
            Title = "Spatial Filter",
            Description = "Passes through only features whose geometry satisfies a spatial predicate against a bounding box or arbitrary WKT region, dropping the rest. Pure managed NetTopologySuite — no native dependency. Features with null/empty geometry are dropped.",
            Category = "transform",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("bbox", "Bounding Box", "Region as 'minX,minY,maxX,maxY' in the feature CRS. Supply this or 'wkt'.", ProcessParameterValueType.Text),
                Param("wkt", "WKT Region", "Region geometry as WKT. Supply this or 'bbox'.", ProcessParameterValueType.Text),
                Param("predicate", "Predicate", "Spatial predicate. Allowed values: intersects (default), within.", ProcessParameterValueType.Text, defaultValue: "intersects"),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "transform.clip",
            Title = "Clip Features",
            Description = "Clips each feature's geometry to an area-of-interest region (the geometric intersection), dropping features that fall entirely outside the region. Pure managed NetTopologySuite overlay — no native dependency. Attributes are preserved and the clipped geometry keeps the source SRID.",
            Category = "transform",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("bbox", "Bounding Box", "Clip region as 'minX,minY,maxX,maxY' in the feature CRS. Supply this or 'wkt'.", ProcessParameterValueType.Text),
                Param("wkt", "WKT Region", "Clip region geometry as WKT. Supply this or 'bbox'.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "transform.dedup",
            Title = "Deduplicate Features",
            Description = "Emits the first feature for each distinct key and drops later duplicates. The key is built from one or more attribute fields, the geometry (normalized WKT), or both. At least one of 'keys' or 'geometry=true' is required.",
            Category = "transform",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("keys", "Key Fields", "Comma-separated attribute field names whose values form the dedup key.", ProcessParameterValueType.Text),
                Param("geometry", "Use Geometry", "Include the normalized geometry in the dedup key.", ProcessParameterValueType.Flag, defaultValue: "false"),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "transform.reproject",
            Title = "Reproject Features",
            Description = "Reprojects every feature's geometry between SRIDs on the managed, GDAL-free path (identity, Web Mercator aliases, and WGS 84 (4326) ↔ Web Mercator), reusing the same in-memory CoordinateTransformer as geometry.project. Datum-shift pairs requiring ST_Transform are deferred to the native worker profile and rejected. Attributes are carried through.",
            Category = "transform",
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("fromSrid", "From SRID", "Source spatial reference identifier.", ProcessParameterValueType.Srid, required: true),
                Param("toSrid", "To SRID", "Target spatial reference identifier.", ProcessParameterValueType.Srid, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },

        // -----------------------------------------------------------------------
        // GeoETL source operations (2)
        // Produce a FeatureCollection artifact from an inline document so the
        // workflow DAG starts from a uniform envelope. Managed parsers only.
        // Native-format sources (shapefile, geopackage) need native libs and are
        // deferred to the GDAL worker stream as gdal.* / native-profile processes.
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "source.geojson",
            Title = "GeoJSON Source",
            Description = "Parses an inline GeoJSON FeatureCollection (or one supplied as a data:application/geo+json;base64 data URI) into the standard FeatureCollection artifact. Managed NetTopologySuite reader — no native dependency.",
            Category = "source",
            Parameters =
            [
                Param("inline", "Inline GeoJSON", "GeoJSON FeatureCollection document supplied directly. Supply this or 'input'.", ProcessParameterValueType.Text),
                Param("input", "Input Data URI", "FeatureCollection as a data:application/geo+json;base64 data URI. Supply this or 'inline'.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "source.csv",
            Title = "CSV Source",
            Description = "Parses an inline CSV document into a FeatureCollection, deriving geometry from a WKT column (wkt/geom/geometry/shape/...) or a longitude/latitude column pair (lon/lng/x and lat/y). Managed parser — no native dependency.",
            Category = "source",
            Parameters =
            [
                Param("inline", "Inline CSV", "CSV document supplied directly, including a header row.", ProcessParameterValueType.Text, required: true),
                Param("delimiter", "Delimiter", "Single-character field delimiter override. Defaults to comma.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },

        // -----------------------------------------------------------------------
        // GeoETL remote source connectors (5)
        // First-class DAG sources that stream features from a remote source straight
        // into the workflow, REUSING the one-shot Honua.Import readers' pagination /
        // streaming (ArcGisRestClient, the OGC/WFS GeoJSON paging path, the catalog
        // query pipeline, and the external-PostGIS secure-connection handling) so a
        // pipeline no longer has to bounce through a one-shot import to read from a
        // Honua layer, Esri FeatureServer, OGC API Features, WFS, or external PostGIS.
        // Each emits the standard FeatureCollection artifact downstream transforms
        // consume. An optional since/watermark is passed through for incremental
        // extract (persistence owned by the incremental-extract orchestration).
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "source.honua-layer",
            Title = "Honua Layer Source",
            Description = "Streams features from a Honua catalog layer through the canonical query pipeline (the layer's permanent filters, paging, CRS handling, and field masking are inherited) into the standard FeatureCollection artifact. Supply a where clause and/or a bbox to narrow the extract.",
            Category = "source",
            Parameters =
            [
                // Declared as LayerId (not WholeNumber) because it IS a catalog layer
                // reference: the submit-time per-layer read gate derives the layers a plan
                // reads from the declared LayerId parameters, and this connector streams a
                // catalog layer's features straight into a job artifact (honua-server#3046).
                Param("layerId", "Layer Id", "Honua catalog layer identifier to read from.", ProcessParameterValueType.LayerId, required: true),
                Param("where", "Where", "Optional GeoServices-style SQL where clause filtering the features.", ProcessParameterValueType.Text),
                Param("bbox", "Bounding Box", "Optional 'minX,minY,maxX,maxY' envelope filter in the output CRS.", ProcessParameterValueType.Text),
                Param("outFields", "Output Fields", "Optional comma-separated output field allow-list. Defaults to all fields.", ProcessParameterValueType.Text),
                Param("outSrid", "Output SRID", "Optional output spatial reference identifier for server-side reprojection.", ProcessParameterValueType.Srid),
                Param("since", "Since Watermark", "Optional incremental-extract watermark (ISO-8601 instant). Pairs with watermarkField.", ProcessParameterValueType.Text),
                Param("watermarkField", "Watermark Field", "Attribute the since watermark filters on (e.g. updated_at).", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "source.esri-featureserver",
            Title = "Esri FeatureServer Source",
            Description = "Streams features from an ArcGIS GeoServices FeatureServer/MapServer layer by reusing the migration ArcGIS REST reader (resultOffset/resultRecordCount paging terminated by exceededTransferLimit) and its Esri-JSON to GeoJSON geometry conversion. Reuses the import path's ArcGIS portal-token / HTTP Basic credential handling.",
            Category = "source",
            Parameters =
            [
                Param("serviceUrl", "Service URL", "ArcGIS FeatureServer/MapServer service root URL.", ProcessParameterValueType.Text, required: true),
                Param("esriLayerId", "Layer Index", "Layer index within the FeatureServer. Defaults to 0.", ProcessParameterValueType.WholeNumber),
                Param("where", "Where", "Optional GeoServices SQL where clause. Defaults to 1=1.", ProcessParameterValueType.Text),
                Param("outFields", "Output Fields", "Optional comma-separated output field allow-list. Defaults to all (*).", ProcessParameterValueType.Text),
                Param("outSrid", "Output SRID", "Optional output spatial reference (outSR).", ProcessParameterValueType.Srid),
                Param("pageSize", "Page Size", "Records per page (resultRecordCount). Defaults to 1000.", ProcessParameterValueType.WholeNumber, defaultValue: "1000"),
                Param("token", "ArcGIS Token", "Inline ArcGIS token for immediate use. Prefer tokenSecretReference.", ProcessParameterValueType.Text),
                Param("tokenSecretReference", "Token Secret Reference", "Secret reference that resolves to an ArcGIS token at execution time.", ProcessParameterValueType.Text),
                Param("username", "Username", "HTTP Basic username for secured ArcGIS endpoints.", ProcessParameterValueType.Text),
                Param("passwordSecretReference", "Password Secret Reference", "Secret reference that resolves to the HTTP Basic password.", ProcessParameterValueType.Text),
                Param("since", "Since Watermark", "Optional incremental-extract watermark (ISO-8601 instant). Pairs with watermarkField.", ProcessParameterValueType.Text),
                Param("watermarkField", "Watermark Field", "Edit-date field the since watermark filters on (e.g. last_edited_date).", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "source.ogc-features",
            Title = "OGC API Features Source",
            Description = "Streams features from an OGC API Features collection using link-based pagination (an items request with limit/bbox/filter followed by the rel=next link chain), reusing the migration HTTP/paging path. The where clause is interpreted as a CQL2-text filter.",
            Category = "source",
            Parameters =
            [
                Param("serviceUrl", "Service URL", "OGC API Features landing/base URL (the part before /collections).", ProcessParameterValueType.Text, required: true),
                Param("collectionId", "Collection Id", "OGC API Features collection identifier.", ProcessParameterValueType.Text, required: true),
                Param("where", "CQL2 Filter", "Optional CQL2-text filter expression.", ProcessParameterValueType.Text),
                Param("bbox", "Bounding Box", "Optional 'minX,minY,maxX,maxY' bbox filter.", ProcessParameterValueType.Text),
                Param("pageSize", "Page Size", "Features per page (limit). Defaults to 1000.", ProcessParameterValueType.WholeNumber, defaultValue: "1000"),
                Param("username", "Username", "Optional HTTP Basic username.", ProcessParameterValueType.Text),
                Param("passwordSecretReference", "Password Secret Reference", "Secret reference that resolves to the HTTP Basic password.", ProcessParameterValueType.Text),
                Param("since", "Since Watermark", "Optional incremental-extract watermark (ISO-8601 instant), mapped to a datetime open interval.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "source.wfs",
            Title = "WFS Source",
            Description = "Streams features from a WFS GetFeature endpoint using startIndex/count paging with GeoJSON output, terminating on an empty page or once advanced past numberMatched (with a repeated-first-feature guard for servers that ignore startIndex). Reuses the migration HTTP/paging path.",
            Category = "source",
            Parameters =
            [
                Param("serviceUrl", "Service URL", "WFS service endpoint URL.", ProcessParameterValueType.Text, required: true),
                Param("typeName", "Type Name", "WFS feature type name (typeNames).", ProcessParameterValueType.Text, required: true),
                Param("bbox", "Bounding Box", "Optional 'minX,minY,maxX,maxY' bbox filter.", ProcessParameterValueType.Text),
                Param("pageSize", "Page Size", "Features per page (count). Defaults to 1000.", ProcessParameterValueType.WholeNumber, defaultValue: "1000"),
                Param("username", "Username", "Optional HTTP Basic username.", ProcessParameterValueType.Text),
                Param("passwordSecretReference", "Password Secret Reference", "Secret reference that resolves to the HTTP Basic password.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "source.postgis",
            Title = "External PostGIS Source",
            Description = "Streams features from a customer-owned PostGIS table/view identified by a registered secure connection — the read-side mirror of sink.external-postgis. Geometry is projected with ST_AsGeoJSON server-side and rows stream through a forward-only reader. Uses the same secure-connection secret handling as the sink; raw connection strings are never accepted.",
            Category = "source",
            Parameters =
            [
                Param("connectionName", "Secure Connection Name", "Registered secure connection name for the external PostGIS database. Either connectionName or connectionId is required.", ProcessParameterValueType.Text),
                Param("connectionId", "Secure Connection Id", "Registered secure connection id for the external PostGIS database. Either connectionName or connectionId is required.", ProcessParameterValueType.Text),
                Param("table", "Table", "Source table or view name. Must match ^[A-Za-z_][A-Za-z0-9_]*$.", ProcessParameterValueType.Text, required: true),
                Param("schema", "Schema", "Source schema. Defaults to public. Must match ^[A-Za-z_][A-Za-z0-9_]*$.", ProcessParameterValueType.Text, defaultValue: "public"),
                Param("geometryColumn", "Geometry Column", "Source geometry column. Defaults to geom. Must match ^[A-Za-z_][A-Za-z0-9_]*$.", ProcessParameterValueType.Text, defaultValue: "geom"),
                Param("where", "Where", "Optional SQL predicate appended after WHERE.", ProcessParameterValueType.Text),
                Param("bbox", "Bounding Box", "Optional 'minX,minY,maxX,maxY' envelope filter (&& ST_MakeEnvelope).", ProcessParameterValueType.Text),
                Param("outSrid", "Bbox SRID", "SRID of the bbox envelope. Defaults to 4326.", ProcessParameterValueType.Srid),
                Param("since", "Since Watermark", "Optional incremental-extract watermark. Pairs with watermarkField.", ProcessParameterValueType.Text),
                Param("watermarkField", "Watermark Field", "Column the since watermark filters on (e.g. updated_at).", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },

        // -----------------------------------------------------------------------
        // GeoETL sink operations (4)
        // Terminate a workflow by writing the input FeatureCollection to a target and
        // emit a small result-descriptor artifact (the target location + row counts).
        // Managed writers / Npgsql only — no GDAL.
        // The catalog honua-layer sink (sink.honua-layer) loads into a named layer in the
        // Honua catalog database. It reaches the catalog NpgsqlDataSource through the
        // optional IHonuaLayerSink capability (#2210), which is registered only when the
        // catalog database is present — so the geoprocessing dispatcher, constructed
        // unconditionally including in lean Postgres-free deployments, never takes a
        // Postgres dependency, and no catalog connection string is leaked through plan
        // parameters. In a lean deployment the capability is absent and the node fails
        // closed with a clear "unavailable in this deployment" message.
        // Native-format sinks (shapefile, geopackage) are deferred to the GDAL stream.
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "sink.geojson-file",
            Title = "GeoJSON File Sink",
            Description = "Writes the input FeatureCollection to a GeoJSON FeatureCollection file below the configured geoprocessing output root, emitting a result descriptor with written/rejected counts. Managed NetTopologySuite writer — no native dependency.",
            Category = "sink",
            ExecutionTier = ProcessExecutionTier.Mutating,
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("path", "Output Path", "Relative output file path under the configured geoprocessing output root (overwritten if it exists).", ProcessParameterValueType.Text, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.File]
        },
        new ProcessDefinition
        {
            ProcessId = "sink.quarantine",
            Title = "Quarantine Sink",
            Description = "Dead-letter sink: writes every input feature to a companion GeoJSON artifact tagged with the run batch id and a rejection reason, never throwing on a malformed row. The sink half of the row-level-error contract.",
            Category = "sink",
            ExecutionTier = ProcessExecutionTier.Mutating,
            Parameters =
            [
                Param("input", "Rejected Features", "Input FeatureCollection of rejected rows as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("path", "Output Path", "Relative dead-letter output file path under the configured geoprocessing output root (overwritten if it exists).", ProcessParameterValueType.Text, required: true),
                Param("reasonField", "Reason Field", "Attribute name carrying a per-row reason string. Defaults to _quarantine_reason.", ProcessParameterValueType.Text, defaultValue: "_quarantine_reason"),
                Param("batchId", "Batch Id", "Run batch identifier tagged on every quarantined row. Defaults to the operation id.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.File]
        },
        new ProcessDefinition
        {
            ProcessId = "sink.external-postgis",
            Title = "External PostGIS Sink",
            Description = "Loads the input FeatureCollection into a customer-owned PostGIS database identified by a registered secure connection. Managed Npgsql + WKB — no GDAL. Every row's attributes JSONB carries a reserved __pipeline_batch_id key for soft-delete rollback.",
            Category = "sink",
            ExecutionTier = ProcessExecutionTier.Mutating,
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("connectionName", "Secure Connection Name", "Registered secure connection name for the external PostGIS database. Either connectionName or connectionId is required.", ProcessParameterValueType.Text),
                Param("connectionId", "Secure Connection Id", "Registered secure connection id for the external PostGIS database. Either connectionName or connectionId is required.", ProcessParameterValueType.Text),
                Param("table", "Table", "Destination table name (created if missing). Must match ^[A-Za-z_][A-Za-z0-9_]*$.", ProcessParameterValueType.Text, required: true),
                Param("targetSrid", "Target SRID", "Geometry SRID for the destination column.", ProcessParameterValueType.Srid, required: true),
                Param("schema", "Schema", "Destination schema. Defaults to public. Must match ^[A-Za-z_][A-Za-z0-9_]*$.", ProcessParameterValueType.Text, defaultValue: "public"),
                Param("geometryColumn", "Geometry Column", "Destination geometry column. Defaults to geom. Must match ^[A-Za-z_][A-Za-z0-9_]*$.", ProcessParameterValueType.Text, defaultValue: "geom"),
                Param("batchSize", "Batch Size", "Insert batch size. Defaults to 1000.", ProcessParameterValueType.WholeNumber, defaultValue: "1000"),
                Param("batchId", "Batch Id", "Run batch id tagged on every row. Defaults to the operation id.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },
        new ProcessDefinition
        {
            ProcessId = "sink.honua-layer",
            Title = "Honua Catalog Layer Sink",
            Description = "Loads the input FeatureCollection into a named layer in the Honua catalog database via the catalog data source. Supports append/replace/upsert load modes; every row's attributes JSONB carries a reserved __pipeline_batch_id key for soft-delete rollback. Requires a configured catalog database — fails closed in lean, database-free deployments. Managed Npgsql + WKB — no GDAL.",
            Category = "sink",
            ExecutionTier = ProcessExecutionTier.Mutating,
            Parameters =
            [
                Param("input", "Input Features", "Input FeatureCollection as a data:application/geo+json;base64 data URI.", ProcessParameterValueType.Text, required: true),
                Param("layer", "Layer Name", "Destination layer/table name in the catalog (created if missing). Must match ^[A-Za-z_][A-Za-z0-9_]*$.", ProcessParameterValueType.Text, required: true),
                Param("targetSrid", "Target SRID", "Geometry SRID for the destination column.", ProcessParameterValueType.Srid, required: true),
                Param("loadMode", "Load Mode", "How incoming rows reconcile with existing rows: append, replace, or upsert. Defaults to append.", ProcessParameterValueType.Text, defaultValue: "append"),
                Param("keyFields", "Key Fields", "Comma-separated attribute names identifying a row for upsert. Required when loadMode is upsert.", ProcessParameterValueType.Text),
                Param("schema", "Schema", "Destination schema. Defaults to public. Must match ^[A-Za-z_][A-Za-z0-9_]*$.", ProcessParameterValueType.Text, defaultValue: "public"),
                Param("geometryColumn", "Geometry Column", "Destination geometry column. Defaults to geom. Must match ^[A-Za-z_][A-Za-z0-9_]*$.", ProcessParameterValueType.Text, defaultValue: "geom"),
                Param("batchId", "Batch Id", "Run batch id tagged on every row. Defaults to the operation id.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        },

        // -----------------------------------------------------------------------
        // Native GDAL worker operations (2)
        // Reconciled from feat/gdal-heavy-worker onto the #1185 contract. These
        // are the ONLY catalog processes that declare RuntimeProfile = native:
        // they execute OUT-OF-PROCESS in the heavyweight GDAL worker image
        // (gdalwarp / ogr2ogr via subprocess), never in the lean GDAL-free serving
        // image. The geoprocessing submit path reads ProcessDefinition.RuntimeProfile
        // to stamp ExecutionJobSpec.RuntimeProfile = "native", so the claim fence
        // routes the job to the GDAL worker and away from the lean dispatcher (which
        // has no executor for these ids). They are the native counterparts to the
        // managed geometry.project / conversion.* idioms, covering the PROJ-backed
        // raster reprojection and OGR vector conversions the managed readers cannot
        // perform. The worker's executors handle exactly these ids
        // (GdalRasterReprojectJobExecutor.HandledProcessId / GdalVectorConvertJobExecutor.HandledProcessId).
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "gdal.gdalwarp",
            Title = "Raster Reproject (GDAL)",
            Description = "Full PROJ-backed raster reprojection executed out-of-process by the heavyweight GDAL worker via the gdalwarp CLI. The native counterpart to the managed geometry.project executor, which rejects datum-shift transforms that require PROJ. Reads a base64 GeoTIFF source and a target CRS (EPSG code or AUTHORITY:CODE) from the durable spec and publishes the reprojected GeoTIFF as a data-URI artifact. Routed to the native worker profile — NOT executable in the GDAL-free serving image.",
            Category = "raster",
            Parameters =
            [
                Param("source", "Source Raster", "Source raster as base64-encoded GeoTIFF bytes.", ProcessParameterValueType.Text, required: true),
                Param("targetSrs", "Target CRS", "Target spatial reference as an EPSG code (e.g. 'EPSG:3857', also written as '3857').", ProcessParameterValueType.Srid, required: true),
                Param("sourceSrs", "Source CRS", "Optional source spatial reference override when the raster lacks embedded CRS metadata.", ProcessParameterValueType.Srid),
            ],
            OutputArtifactKinds = [ArtifactKind.Raster],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "gdal.ogr2ogr",
            Title = "Vector Convert (GDAL)",
            Description = "OGR vector format conversion executed out-of-process by the heavyweight GDAL worker via the ogr2ogr CLI, for formats the managed NetTopologySuite readers cannot handle. Reads a base64 source dataset and a target OGR driver from the durable spec and publishes the converted bytes as a data-URI artifact. Supported target drivers: GeoJSON, GPKG, CSV, FlatGeobuf, ESRI Shapefile. Routed to the native worker profile — NOT executable in the GDAL-free serving image.",
            Category = "conversion",
            Parameters =
            [
                Param("source", "Source Dataset", "Source vector dataset as base64-encoded bytes in the source format.", ProcessParameterValueType.Text, required: true),
                Param("targetFormat", "Target Format", "Target OGR driver. Allowed values: GeoJSON, GPKG, CSV, FlatGeobuf, ESRI Shapefile.", ProcessParameterValueType.Text, required: true),
                Param("sourceFormat", "Source Format", "Source OGR driver hint used to choose the input file extension. Defaults to GeoJSON.", ProcessParameterValueType.Text, defaultValue: "GeoJSON"),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer],
            RuntimeProfile = RuntimeProfiles.Native
        },
        new ProcessDefinition
        {
            ProcessId = "source.ogr",
            Title = "OGR Source (GDAL)",
            Description = "GDAL/OGR-backed import reader executed out-of-process by the heavyweight worker via the ogr2ogr CLI. The native counterpart to the managed source.geojson / source.csv readers (hand-rolled NetTopologySuite parsers limited to GeoJSON and CSV): source.ogr canonicalizes the FULL OGR driver universe — native File Geodatabase (OpenFileGDB), GML / GML application schemas, KML, MapInfo TAB, ESRI Shapefile, GeoPackage, FlatGeobuf, and (where the worker image's drivers are compiled in) database-spatial sources — into the standard GeoJSON FeatureCollection artifact every workflow DAG starts from. Multi-file datasets (Shapefile sidecars, FileGDB directories) are supplied as a base64 ZIP and unpacked in an isolated scratch workspace. Routed to the native worker profile — NOT executable in the GDAL-free serving image.",
            Category = "source",
            Parameters =
            [
                Param("source", "Source Dataset", "Source dataset as base64-encoded bytes in the source format. Multi-file datasets (Shapefile, FileGDB) are supplied as a base64-encoded ZIP archive, which the worker unpacks before opening with OGR.", ProcessParameterValueType.Text, required: true),
                Param("sourceFormat", "Source Format", "Source OGR driver hint used to choose the input file extension when the payload is a single file. Allowed values include: GeoJSON, GML, KML, GPKG, FlatGeobuf, MapInfo File, CSV, ESRI Shapefile, OpenFileGDB. Defaults to GeoJSON. ZIP-packaged datasets are detected by content and the hint is advisory.", ProcessParameterValueType.Text, defaultValue: "GeoJSON"),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer],
            RuntimeProfile = RuntimeProfiles.Native
        },

        // -----------------------------------------------------------------------
        // Point-cloud conversion (1)
        // LAZ/COPC decompression + optional projected-CRS reprojection executed
        // out-of-process by the heavyweight GDAL/PDAL worker via the `pdal
        // translate` CLI (#1854). Declared RuntimeProfile = native so the submit
        // path stamps ExecutionJobSpec.RuntimeProfile = "native" and the claim
        // fence routes the job to the worker (PdalPointCloudConvertJobExecutor
        // handles exactly this id) and away from the lean dispatcher, which has no
        // executor for it. The native counterpart to the pure-managed LAS reader
        // (LasPointCloudReader, #1840): the worker decompresses the cloud-optimized
        // / arithmetic-coded chunks the managed reader cannot decode and, when a
        // projected source CRS is supplied, reprojects to geographic EPSG:4979,
        // returning uncompressed LAS the managed scene tiler then turns into 3D
        // Tiles. The lean image validates these plans (parameter shape + the
        // sourceSrs token guard) but never executes them.
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "pcloud.translate",
            Title = "Point Cloud Translate (LAZ/COPC → LAS)",
            Description = "Decompresses a LAZ or Cloud-Optimized Point Cloud (COPC) source and, when a projected source CRS is supplied, reprojects its horizontal datum to geographic EPSG:4979, emitting an UNCOMPRESSED LAS artifact. Executed out-of-process by the heavyweight GDAL/PDAL worker via the `pdal translate` CLI (built on laz-perf for LAZ/COPC decoding and PROJ for reprojection). The native counterpart to the pure-managed LasPointCloudReader, which accepts uncompressed LAS only: this process produces the uncompressed LAS the managed scene tiler turns into 3D Tiles. Reads a base64-encoded LAZ/COPC source from 'source' and an optional source CRS token from 'sourceSrs'; publishes the decompressed LAS as a data-URI artifact. Routed to the native worker profile — NOT executable in the GDAL-free serving image.",
            Category = "conversion",
            Parameters =
            [
                Param("source", "Source Point Cloud", "Source point cloud as base64-encoded LAZ or COPC bytes.", ProcessParameterValueType.Text, required: true),
                Param("sourceSrs", "Source CRS", "Optional source spatial reference token (an EPSG code like '32610'/'EPSG:32610' or an AUTHORITY:CODE token). When supplied and not already geographic, the worker reprojects the cloud to geographic EPSG:4979 before emitting LAS; a geographic source is decompressed verbatim. Omit it for a cloud already in geographic coordinates.", ProcessParameterValueType.Text),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer],
            RuntimeProfile = RuntimeProfiles.Native
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

    // Native worker raster source selector for surface.* and raster.* entries.
    // The native GDAL worker reads a base64 GeoTIFF directly from 'source'. As of
    // #2264 the submit path also resolves a registered catalog raster from
    // `layerId`/`rasterId` and materializes it onto `source` before dispatch, so a
    // plan must supply EXACTLY ONE of: an inline `source`, a `layerId`, or a
    // `rasterId`. `source` is therefore declared OPTIONAL and the "supply one of"
    // rule is enforced by ValidateSharedRasterSourceSemantics so a plan that omits
    // all three is rejected at submit time rather than failing in the worker.
    private static readonly ProcessParameterSpec[] NativeRasterSourceParameters =
    [
        Param("source", "Source Raster", "Source raster as base64-encoded GeoTIFF bytes. Supply this OR a layerId/rasterId that resolves to a registered catalog raster.", ProcessParameterValueType.Text),
        Param("layerId", "Layer", "Catalog raster layer identifier. Resolved at submit time to the layer's registered raster (newest registration when several exist). Supply this OR an inline source / rasterId.", ProcessParameterValueType.LayerId),
        Param("rasterId", "Raster", "Registered raster identifier. Resolved at submit time to the registered raster bytes. Supply this OR an inline source / layerId. When supplied, it must be a positive 64-bit integer.", ProcessParameterValueType.Text),
    ];

    private static ProcessParameterSpec Param(
        string name,
        string displayName,
        string description,
        ProcessParameterValueType valueType,
        bool required = false,
        string? defaultValue = null,
        IReadOnlyList<string>? allowedValues = null,
        ProcessLayerAccess layerAccess = ProcessLayerAccess.Read) => new()
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            ValueType = valueType,
            Required = required,
            DefaultValue = defaultValue,
            AllowedValues = allowedValues,
            LayerAccess = layerAccess
        };
}
