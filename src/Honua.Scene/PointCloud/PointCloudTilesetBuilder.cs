// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;

namespace Honua.Core.Features.Scene.PointCloud;

/// <summary>
/// Converts a decoded LAS point cloud into a deterministic 3D Tiles point
/// tileset: a quadtree of <c>.pnts</c> tiles plus a <c>tileset.json</c> tree
/// servable through the existing scene infrastructure (#1201).
/// </summary>
/// <remarks>
/// <para>
/// Points are projected to geographic coordinates by a caller-supplied
/// <see cref="PointCloudGeoTransform"/> (which interprets the LAS file's CRS),
/// partitioned by their lon/lat into a quadtree via the same deterministic
/// scheme used for feature tiling (#1200), and each content node is written as a
/// <c>.pnts</c> tile whose positions are ECEF-encoded with an RTC center.
/// Classification, intensity, and RGB are preserved per point.
/// </para>
/// <para>
/// Output is byte-stable: points keep their LAS file order, tile uris are
/// assigned in a fixed pre-order walk, and the writer emits fixed property
/// orders, so identical input produces identical bytes across runs.
/// </para>
/// </remarks>
public static class PointCloudTilesetBuilder
{
    /// <summary>
    /// Builds the in-memory artifacts (tile uris -> bytes, plus the tileset
    /// document bytes) for a LAS point cloud. The caller writes the returned
    /// blobs under the scene asset root.
    /// </summary>
    /// <param name="points">Decoded LAS points in file order. Must be non-empty.</param>
    /// <param name="transform">Projects a LAS (x, y, z) to (lon°, lat°, heightMeters).</param>
    /// <param name="options">Tiling thresholds (max points per tile, depth).</param>
    /// <param name="generatorTag">Optional generator label for the tileset asset block.</param>
    /// <returns>The deterministic point-cloud tileset artifacts.</returns>
    public static PointCloudTilesetResult Build(
        IReadOnlyList<LasPoint> points,
        PointCloudGeoTransform transform,
        PointCloudTilingOptions options,
        string? generatorTag = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(options);
        if (points.Count == 0)
        {
            throw new ArgumentException("At least one point is required to build a point-cloud tileset.", nameof(points));
        }

        // Perf follow-up: the builder buffers the whole cloud into a
        // ProjectedPoint[] and the quadtree recursion allocates a fresh
        // List<int> per node plus members.ToArray() per child, so index storage
        // is duplicated at each depth. A bounded improvement would partition a
        // single reusable index buffer in place (range-based (begin,end) splits
        // with an in-array swap) and stream/spool the source points, capping the
        // point count to avoid a malformed LAS exhausting memory. Deferred to
        // avoid destabilizing the current deterministic layout; this method's
        // IReadOnlyList contract already requires random access.

        // 1. Project every point once; track the geographic envelope.
        var projected = new ProjectedPoint[points.Count];
        var west = double.PositiveInfinity;
        var south = double.PositiveInfinity;
        var east = double.NegativeInfinity;
        var north = double.NegativeInfinity;
        var minHeight = double.PositiveInfinity;
        var maxHeight = double.NegativeInfinity;

        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var (lon, lat, height) = transform(p.X, p.Y, p.Z);
            var (ex, ey, ez) = EcefCoordinateTransform.ToEcef(lon, lat, height);
            projected[i] = new ProjectedPoint(lon, lat, height, ex, ey, ez, p);

            if (lon < west) west = lon;
            if (lat < south) south = lat;
            if (lon > east) east = lon;
            if (lat > north) north = lat;
            if (height < minHeight) minHeight = height;
            if (height > maxHeight) maxHeight = height;
        }

        var boundsDegrees = new[] { west, south, east, north };
        var rootGeometricError = ComputeRootGeometricError(boundsDegrees, minHeight, maxHeight);

        // 2. Partition by lon/lat into a quadtree of point indices.
        var indices = new int[projected.Length];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = i;
        }

        var root = BuildNode(
            projected,
            indices,
            west, south, east, north,
            depth: 0,
            geometricError: rootGeometricError,
            options);

        // 3. Walk pre-order, emit one .pnts per content-bearing node.
        var contentNodes = new List<PointTileNode>();
        CollectContentNodes(root, contentNodes);

        var tiles = new Dictionary<string, byte[]>(contentNodes.Count, StringComparer.Ordinal);
        var content = new Dictionary<SceneTileNode, SceneTileContent>(contentNodes.Count);

        for (var i = 0; i < contentNodes.Count; i++)
        {
            var node = contentNodes[i];
            var uri = $"points_{i:0000}.pnts";
            var pnts = PntsTileWriter.Build(node.Points);
            tiles[uri] = pnts;
            content[node.SceneNode] = new SceneTileContent(uri, node.MinHeight, node.MaxHeight);
        }

        var tree = new SceneTileTree(BuildSceneNode(root), rootGeometricError);
        var tileset = TilesetDocumentWriter.BuildTree(tree, content, generatorTag);
        var tilesetBytes = TilesetDocumentWriter.Serialize(tileset);

        return new PointCloudTilesetResult(tilesetBytes, tiles, boundsDegrees, contentNodes.Count, points.Count);
    }

    private static PointTileNode BuildNode(
        ProjectedPoint[] projected,
        int[] memberIndices,
        double west,
        double south,
        double east,
        double north,
        int depth,
        double geometricError,
        PointCloudTilingOptions options)
    {
        var sceneNode = new SceneTileNode(west, south, east, north, geometricError, depth, Array.Empty<SceneFeature>(), Array.Empty<SceneTileNode>());

        var leafReached = memberIndices.Length <= options.MaxPointsPerTile;
        var depthReached = depth >= options.MaxDepth;
        if (leafReached || depthReached)
        {
            return MakeLeaf(projected, memberIndices, west, south, east, north, depth, sceneNode);
        }

        var midLon = (west + east) * 0.5;
        var midLat = (south + north) * 0.5;
        var sw = new List<int>();
        var se = new List<int>();
        var nw = new List<int>();
        var ne = new List<int>();
        foreach (var index in memberIndices)
        {
            var p = projected[index];
            var isEast = p.Lon > midLon;
            var isNorth = p.Lat > midLat;
            if (isNorth)
            {
                (isEast ? ne : nw).Add(index);
            }
            else
            {
                (isEast ? se : sw).Add(index);
            }
        }

        var nonEmpty =
            (sw.Count > 0 ? 1 : 0) + (se.Count > 0 ? 1 : 0) +
            (nw.Count > 0 ? 1 : 0) + (ne.Count > 0 ? 1 : 0);
        if (nonEmpty <= 1)
        {
            return MakeLeaf(projected, memberIndices, west, south, east, north, depth, sceneNode);
        }

        var childError = geometricError * 0.5;
        var children = new List<PointTileNode>(4);
        AddChild(children, projected, sw, west, south, midLon, midLat, depth + 1, childError, options);
        AddChild(children, projected, se, midLon, south, east, midLat, depth + 1, childError, options);
        AddChild(children, projected, nw, west, midLat, midLon, north, depth + 1, childError, options);
        AddChild(children, projected, ne, midLon, midLat, east, north, depth + 1, childError, options);

        // Interior coarse LOD: deterministic strided sample of the member points.
        var sample = SamplePoints(projected, memberIndices, options.InteriorSampleCount);
        var (sampleMin, sampleMax) = HeightExtent(projected, memberIndices);

        var sceneChildren = new SceneTileNode[children.Count];
        for (var i = 0; i < children.Count; i++)
        {
            sceneChildren[i] = children[i].SceneNode;
        }
        var interiorSceneNode = new SceneTileNode(west, south, east, north, geometricError, depth, Array.Empty<SceneFeature>(), sceneChildren);

        return new PointTileNode
        {
            SceneNode = interiorSceneNode,
            Points = sample,
            Children = children,
            MinHeight = sampleMin,
            MaxHeight = sampleMax
        };
    }

    private static void AddChild(
        List<PointTileNode> children,
        ProjectedPoint[] projected,
        List<int> members,
        double west,
        double south,
        double east,
        double north,
        int depth,
        double geometricError,
        PointCloudTilingOptions options)
    {
        if (members.Count == 0)
        {
            return;
        }
        children.Add(BuildNode(projected, members.ToArray(), west, south, east, north, depth, geometricError, options));
    }

    private static PointTileNode MakeLeaf(
        ProjectedPoint[] projected,
        int[] memberIndices,
        double west,
        double south,
        double east,
        double north,
        int depth,
        SceneTileNode sceneNode)
    {
        var pts = MaterializePoints(projected, memberIndices);
        var (min, max) = HeightExtent(projected, memberIndices);
        return new PointTileNode
        {
            SceneNode = sceneNode,
            Points = pts,
            Children = new List<PointTileNode>(),
            MinHeight = min,
            MaxHeight = max
        };
    }

    private static PntsPoint[] MaterializePoints(ProjectedPoint[] projected, int[] memberIndices)
    {
        var result = new PntsPoint[memberIndices.Length];
        for (var i = 0; i < memberIndices.Length; i++)
        {
            result[i] = projected[memberIndices[i]].ToPnts();
        }
        return result;
    }

    private static PntsPoint[] SamplePoints(ProjectedPoint[] projected, int[] memberIndices, int sampleCount)
    {
        if (sampleCount <= 0 || memberIndices.Length <= sampleCount)
        {
            return MaterializePoints(projected, memberIndices);
        }
        var sample = new PntsPoint[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var sourceIndex = (int)((long)i * memberIndices.Length / sampleCount);
            sample[i] = projected[memberIndices[sourceIndex]].ToPnts();
        }
        return sample;
    }

    private static (double Min, double Max) HeightExtent(ProjectedPoint[] projected, int[] memberIndices)
    {
        if (memberIndices.Length == 0)
        {
            return (0.0, 0.0);
        }
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        foreach (var index in memberIndices)
        {
            var h = projected[index].Height;
            if (h < min) min = h;
            if (h > max) max = h;
        }
        return (min, max);
    }

    private static SceneTileNode BuildSceneNode(PointTileNode node) => node.SceneNode;

    private static void CollectContentNodes(PointTileNode node, List<PointTileNode> sink)
    {
        if (node.Points.Length > 0)
        {
            sink.Add(node);
        }
        foreach (var child in node.Children)
        {
            CollectContentNodes(child, sink);
        }
    }

    private static double ComputeRootGeometricError(double[] boundsDegrees, double minHeight, double maxHeight)
    {
        var lonSpanRad = (boundsDegrees[2] - boundsDegrees[0]) * Math.PI / 180.0;
        var latSpanRad = (boundsDegrees[3] - boundsDegrees[1]) * Math.PI / 180.0;
        var lonMeters = Math.Abs(lonSpanRad) * EcefCoordinateTransform.WgsSemiMajorAxis
            * Math.Cos((boundsDegrees[1] + boundsDegrees[3]) * 0.5 * Math.PI / 180.0);
        var latMeters = Math.Abs(latSpanRad) * EcefCoordinateTransform.WgsSemiMajorAxis;
        var heightMeters = Math.Max(0.0, maxHeight - minHeight);
        var diagonal = Math.Sqrt(lonMeters * lonMeters + latMeters * latMeters + heightMeters * heightMeters);
        return Math.Round(diagonal, 6, MidpointRounding.AwayFromZero);
    }

    private readonly record struct ProjectedPoint(
        double Lon,
        double Lat,
        double Height,
        double EcefX,
        double EcefY,
        double EcefZ,
        LasPoint Source)
    {
        public PntsPoint ToPnts() => new(
            EcefX, EcefY, EcefZ,
            Source.Intensity,
            Source.Classification,
            Source.HasColor,
            Source.Red,
            Source.Green,
            Source.Blue);
    }

    private sealed class PointTileNode
    {
        public required SceneTileNode SceneNode { get; init; }
        public required PntsPoint[] Points { get; init; }
        public required List<PointTileNode> Children { get; init; }
        public required double MinHeight { get; init; }
        public required double MaxHeight { get; init; }
    }
}

/// <summary>
/// Projects a LAS (x, y, z) coordinate to (longitude°, latitude°, height m).
/// Implementations interpret the LAS file's CRS.
/// </summary>
/// <param name="x">LAS X in the file's horizontal units.</param>
/// <param name="y">LAS Y in the file's horizontal units.</param>
/// <param name="z">LAS Z (elevation) in meters.</param>
/// <returns>Geographic longitude/latitude in degrees and ellipsoidal height in meters.</returns>
public delegate (double Longitude, double Latitude, double Height) PointCloudGeoTransform(double x, double y, double z);

/// <summary>
/// Tiling thresholds for the point-cloud quadtree (#1201).
/// </summary>
public sealed record PointCloudTilingOptions
{
    /// <summary>Maximum points a leaf tile may hold before subdivision.</summary>
    public int MaxPointsPerTile { get; init; } = 50_000;

    /// <summary>Hard cap on quadtree depth.</summary>
    public int MaxDepth { get; init; } = 16;

    /// <summary>Representative points carried by each interior node as a coarse LOD.</summary>
    public int InteriorSampleCount { get; init; } = 8_192;
}

/// <summary>
/// Deterministic artifacts produced from a LAS point cloud (#1201): the
/// serialized <c>tileset.json</c> bytes plus a map of relative <c>.pnts</c> uris
/// to their bytes, and dataset summary fields.
/// </summary>
/// <param name="TilesetJsonBytes">Serialized tileset document.</param>
/// <param name="Tiles">Map of relative <c>.pnts</c> uri to tile bytes.</param>
/// <param name="BoundsDegrees">Dataset envelope [west, south, east, north] in degrees.</param>
/// <param name="TileCount">Number of <c>.pnts</c> tiles written.</param>
/// <param name="PointCount">Total points ingested.</param>
public readonly record struct PointCloudTilesetResult(
    byte[] TilesetJsonBytes,
    IReadOnlyDictionary<string, byte[]> Tiles,
    double[] BoundsDegrees,
    int TileCount,
    int PointCount);
