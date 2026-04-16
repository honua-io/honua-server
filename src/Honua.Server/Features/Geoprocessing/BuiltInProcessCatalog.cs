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
        // Geometry operations (10)
        // -----------------------------------------------------------------------
        new ProcessDefinition
        {
            ProcessId = "geometry.buffer",
            Title = "Buffer",
            Description = "Creates a polygon at a specified distance around each input geometry.",
            Category = "geometry",
            Parameters =
            [
                Param("wkb", "Input Geometry", "Geometry to buffer in WKB format.", ProcessParameterValueType.Wkb, required: true),
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
            Description = "Generalizes geometries by removing vertices within the given tolerance, optionally preserving topology.",
            Category = "geometry",
            Parameters =
            [
                Param("wkb", "Input Geometry", "Geometry to simplify in WKB format.", ProcessParameterValueType.Wkb, required: true),
                Param("tolerance", "Tolerance", "Simplification tolerance in spatial reference units.", ProcessParameterValueType.FloatingPoint, required: true),
                Param("preserveTopology", "Preserve Topology", "Use topology-preserving simplification.", ProcessParameterValueType.Flag, defaultValue: "true"),
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
                Param("wkb", "Input Geometry", "Geometry to reproject in WKB format.", ProcessParameterValueType.Wkb, required: true),
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
                Param("wkb", "Input Geometry", "Geometry to repair in WKB format.", ProcessParameterValueType.Wkb, required: true),
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
                Param("wkbs", "Input Geometries", "Array of geometries to union in WKB format.", ProcessParameterValueType.WkbArray, required: true),
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
                Param("targetWkb", "Target Geometry", "Target geometry in WKB format.", ProcessParameterValueType.Wkb, required: true),
                Param("intersectorWkb", "Intersector Geometry", "Intersector geometry in WKB format.", ProcessParameterValueType.Wkb, required: true),
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
                Param("targetWkb", "Target Geometry", "Target geometry in WKB format.", ProcessParameterValueType.Wkb, required: true),
                Param("clipEnvelopeWkb", "Clip Envelope", "Clipping geometry whose bounding envelope is used.", ProcessParameterValueType.Wkb, required: true),
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
                Param("targetWkb", "Target Geometry", "Target geometry in WKB format.", ProcessParameterValueType.Wkb, required: true),
                Param("eraserWkb", "Eraser Geometry", "Geometry to subtract in WKB format.", ProcessParameterValueType.Wkb, required: true),
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
                Param("wkb", "Input Geometry", "Polygon geometry in WKB format.", ProcessParameterValueType.Wkb, required: true),
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
                Param("wkb", "Input Geometry", "Line geometry in WKB format.", ProcessParameterValueType.Wkb, required: true),
                Param("srid", "Spatial Reference", "SRID of the input geometry.", ProcessParameterValueType.Srid, required: true),
            ],
            OutputArtifactKinds = [ArtifactKind.Scalar]
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
