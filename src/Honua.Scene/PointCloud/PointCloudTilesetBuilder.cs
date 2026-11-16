// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;

namespace Honua.Core.Features.Scene.PointCloud;

public static class PointCloudTilesetBuilder
{
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

        // Hard cap on total points enforced BEFORE the O(N) ProjectedPoint[]
        // allocation below, so a malformed/oversized LAS cannot drive an
        // unbounded multi-GB buffer. Rejected with the stable scene error code
        // (the same surface LasPointCloudReader uses) rather than an OOM.
        if (points.Count > options.MaxPointCount)
        {
            throw new LasFormatException(
                SceneGenerationErrorCodes.ModelAssetInvalid,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Point cloud has {0} points, exceeding the maximum of {1}.",
                    points.Count,
                    options.MaxPointCount));
        }

        // Perf follow-up: the builder buffers the whole cloud into a
        // ProjectedPoint[] and the quadtree recursion allocates a fresh
        // List<int> per node plus members.ToArray() per child, so index storage
        // is duplicated at each depth. A bounded improvement would partition a
        // single reusable index buffer in place (range-based (begin,end) splits
        // with an in-array swap) and stream/spool the source points. The total
        // point count is already capped above (PointCloudTilingOptions.MaxPointCount)
        // so a malformed LAS cannot exhaust memory; the remaining work is the
        // in-place index partitioning, deferred to avoid destabilizing the
        // current deterministic layout (this method's IReadOnlyList contract
        // already requires random access).

        // 1. Project every point once; track the geographic envelope.
        var projected = new ProjectedPoint[points.Count];
        var west = double.PositiveInfinity;
        var south = double.PositiveInfinity;
        var east = double.NegativeInfinity;
        var north = double.NegativeInfinity;
        var minHeight = double.PositiveInfinity;
        var maxHeight = double.NegativeInfinity;

        // Decide the colour depth ONCE for the whole cloud. LAS stores colour in
        // 16-bit fields, but many producers stuff unscaled 8-bit values there; a
        // blind >>8 would map those to black. Scan every coloured point: if EVERY
        // channel across the dataset is <= 255 the cloud is 8-bit-in-16-bit and
        // each tile copies the low byte verbatim, otherwise it is genuine 16-bit
        // and every tile >>8-scales. Deciding per-cloud (not per-tile) keeps the
        // encoding identical across leaves and interior coarse-LOD samples, so no
        // colour seam appears at tile boundaries.
        var eightBitColor = true;

        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var (lon, lat, height) = transform(p.X, p.Y, p.Z);
            var (ex, ey, ez) = EcefCoordinateTransform.ToEcef(lon, lat, height);
            projected[i] = new ProjectedPoint(lon, lat, height, ex, ey, ez, p);

            if (p.HasColor && (p.Red > 255 || p.Green > 255 || p.Blue > 255))
            {
                eightBitColor = false;
            }

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
            var uri = string.Create(CultureInfo.InvariantCulture, $"points_{i:0000}.pnts");
            var pnts = PntsTileWriter.Build(node.Points, eightBitColor);
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
        var leafReached = memberIndices.Length <= options.MaxPointsPerTile;
        var depthReached = depth >= options.MaxDepth;
        if (leafReached || depthReached)
        {
            return MakeLeaf(projected, memberIndices, west, south, east, north, depth, geometricError);
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
            return MakeLeaf(projected, memberIndices, west, south, east, north, depth, geometricError);
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
        double inheritedGeometricError)
    {
        // A leaf has no children, so its geometric error must be 0.0: in 3D
        // Tiles a tile's geometricError is the error introduced when its
        // children are not rendered, so a positive value on a childless tile
        // tells the client finer detail still exists and screen-space-error
        // refinement never converges at the deepest LOD. Mirrors the feature
        // pipeline (SceneQuadtreePartitioner.BuildNode leaf).
        //
        // Exception: the root tile (depth 0) keeps its (floored, positive) root
        // geometric error even when it is also a leaf — a zero root error defeats
        // screen-space-error refinement of the root itself and can leave a client
        // never scheduling the root tile (see ComputeRootGeometricError).
        var leafError = depth == 0 ? inheritedGeometricError : 0.0;
        var sceneNode = new SceneTileNode(west, south, east, north, leafError, depth, Array.Empty<SceneFeature>(), Array.Empty<SceneTileNode>());
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
        // A non-positive sample count disables interior content entirely so the
        // interior node carries no points (skipped by content emission), keeping
        // the tileset to leaf geometry. Distinct from the member-set-at-or-below-
        // cap case where the full set IS the representative sample.
        if (sampleCount <= 0)
        {
            return Array.Empty<PntsPoint>();
        }
        if (memberIndices.Length <= sampleCount)
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
        foreach (var h in memberIndices.Select(index => projected[index].Height))
        {
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
        // Shared cos-lat-corrected diagonal geodesy (see GeodesicError); the floor,
        // 6-decimal rounding, and height term all live in the one helper so the
        // point-cloud, BSL, and I3S builders stay numerically identical.
        => GeodesicError.RootGeometricError(boundsDegrees, minHeight, maxHeight);

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

public delegate (double Longitude, double Latitude, double Height) PointCloudGeoTransform(double x, double y, double z);

public sealed record PointCloudTilingOptions
{
    public int MaxPointsPerTile { get; init; } = 50_000;

    public int MaxDepth { get; init; } = 16;

    public int InteriorSampleCount { get; init; } = 8_192;

    public long MaxPointCount { get; init; } = 250_000_000L;
}

public readonly record struct PointCloudTilesetResult(
    byte[] TilesetJsonBytes,
    IReadOnlyDictionary<string, byte[]> Tiles,
    double[] BoundsDegrees,
    int TileCount,
    int PointCount);
