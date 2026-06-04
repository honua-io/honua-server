// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Generation;

/// <summary>
/// Deterministic quadtree spatial partitioner for the 3D Tiles generation
/// pipeline (#1200). Splits an ordered, homogeneous feature set into a
/// hierarchy of tiles with bounding volumes and a per-level geometric error,
/// enabling progressive LOD streaming for city-scale datasets.
/// </summary>
/// <remarks>
/// <para>
/// The partitioner assigns every feature to a leaf by recursively subdividing
/// the dataset's geographic envelope into four equal lon/lat quadrants
/// (NW, NE, SW, SE) until either a leaf holds at most
/// <see cref="SceneLodOptions.MaxFeaturesPerTile"/> features or the tree reaches
/// <see cref="SceneLodOptions.MaxDepth"/>. Feature-to-quadrant assignment uses
/// each feature's geometric centroid so a feature lands in exactly one leaf.
/// </para>
/// <para>
/// Refinement is "REPLACE": every interior node carries a thinned representative
/// sample of the features beneath it (a coarse LOD), and the geometric error
/// strictly decreases from root to leaf so a 3D Tiles client refines as the
/// camera approaches. Sampling is a deterministic stride over the
/// primary-key-ordered features, so the output is byte-stable across runs given
/// identical input.
/// </para>
/// <para>
/// The partition is purely combinatorial — it does not project coordinates or
/// build GLB content. The executor walks the resulting <see cref="SceneTileTree"/>,
/// builds one GLB per node from <see cref="SceneTileNode.Features"/>, and emits a
/// matching <c>tileset.json</c> tree via <see cref="TilesetDocumentWriter"/>.
/// </para>
/// </remarks>
public static class SceneQuadtreePartitioner
{
    /// <summary>
    /// Partitions the supplied features into a quadtree LOD hierarchy.
    /// </summary>
    /// <param name="features">
    /// Ordered, homogeneous-geometry features. Ordering (typically by primary
    /// key) is the determinism contract; the partitioner never reorders within
    /// a leaf.
    /// </param>
    /// <param name="boundsDegrees">
    /// Dataset envelope in WGS-84 degrees: [west, south, east, north]. Must
    /// enclose every feature centroid.
    /// </param>
    /// <param name="rootGeometricError">
    /// Geometric error (meters) declared on the root tile — typically the
    /// dataset's bounding-diagonal length. Child errors are derived by halving
    /// at each level so error decreases monotonically with depth.
    /// </param>
    /// <param name="options">Partitioning thresholds.</param>
    /// <returns>A deterministic <see cref="SceneTileTree"/>.</returns>
    public static SceneTileTree Partition(
        IReadOnlyList<SceneFeature> features,
        double[] boundsDegrees,
        double rootGeometricError,
        SceneLodOptions options)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(boundsDegrees);
        ArgumentNullException.ThrowIfNull(options);
        if (boundsDegrees.Length != 4)
        {
            throw new ArgumentException("boundsDegrees must have exactly 4 elements.", nameof(boundsDegrees));
        }
        if (features.Count == 0)
        {
            throw new ArgumentException("At least one feature is required to partition.", nameof(features));
        }

        // Index features by their assigned leaf path during recursion. We carry
        // the original index so feature ordering inside any node is a stable
        // ascending sequence (preserving the caller's primary-key order).
        var indices = new int[features.Count];
        for (var i = 0; i < features.Count; i++)
        {
            indices[i] = i;
        }

        var centroids = new (double Lon, double Lat)[features.Count];
        for (var i = 0; i < features.Count; i++)
        {
            centroids[i] = ComputeCentroid(features[i].Geometry.Vertices);
        }

        var root = BuildNode(
            features,
            centroids,
            indices,
            west: boundsDegrees[0],
            south: boundsDegrees[1],
            east: boundsDegrees[2],
            north: boundsDegrees[3],
            depth: 0,
            geometricError: rootGeometricError,
            options);

        return new SceneTileTree(root, rootGeometricError);
    }

    private static SceneTileNode BuildNode(
        IReadOnlyList<SceneFeature> features,
        (double Lon, double Lat)[] centroids,
        int[] memberIndices,
        double west,
        double south,
        double east,
        double north,
        int depth,
        double geometricError,
        SceneLodOptions options)
    {
        var leafThresholdReached = memberIndices.Length <= options.MaxFeaturesPerTile;
        var depthLimitReached = depth >= options.MaxDepth;

        if (leafThresholdReached || depthLimitReached)
        {
            // Leaf: carries every member feature in ascending original order.
            var leafFeatures = MaterializeFeatures(features, memberIndices);
            return new SceneTileNode(
                west, south, east, north,
                geometricError: 0.0,
                depth,
                leafFeatures,
                children: Array.Empty<SceneTileNode>());
        }

        // Interior node: split into four lon/lat quadrants about the envelope
        // midpoint. A feature is assigned to the quadrant containing its
        // centroid; midpoint ties bias toward the lower/left quadrant so the
        // assignment is total and deterministic.
        var midLon = (west + east) * 0.5;
        var midLat = (south + north) * 0.5;

        var sw = new List<int>();
        var se = new List<int>();
        var nw = new List<int>();
        var ne = new List<int>();

        foreach (var index in memberIndices)
        {
            var (lon, lat) = centroids[index];
            var isEast = lon > midLon;
            var isNorth = lat > midLat;
            if (isNorth)
            {
                (isEast ? ne : nw).Add(index);
            }
            else
            {
                (isEast ? se : sw).Add(index);
            }
        }

        // If the split fails to separate features (all centroids coincide, or
        // every feature falls into one quadrant past the depth budget), emit a
        // leaf rather than recursing forever.
        var nonEmptyQuadrants =
            (sw.Count > 0 ? 1 : 0) +
            (se.Count > 0 ? 1 : 0) +
            (nw.Count > 0 ? 1 : 0) +
            (ne.Count > 0 ? 1 : 0);
        if (nonEmptyQuadrants <= 1)
        {
            var leafFeatures = MaterializeFeatures(features, memberIndices);
            return new SceneTileNode(
                west, south, east, north,
                geometricError: 0.0,
                depth,
                leafFeatures,
                children: Array.Empty<SceneTileNode>());
        }

        var childError = geometricError * 0.5;
        var children = new List<SceneTileNode>(4);

        // Children are emitted in a fixed quadrant order (SW, SE, NW, NE) so the
        // tileset tree shape and content uris are deterministic.
        AddChild(children, features, centroids, sw, west, south, midLon, midLat, depth + 1, childError, options);
        AddChild(children, features, centroids, se, midLon, south, east, midLat, depth + 1, childError, options);
        AddChild(children, features, centroids, nw, west, midLat, midLon, north, depth + 1, childError, options);
        AddChild(children, features, centroids, ne, midLon, midLat, east, north, depth + 1, childError, options);

        // The interior node itself carries a thinned representative sample as a
        // coarse LOD (REPLACE refinement). Sampling strides the member set so
        // the sample is deterministic and spatially spread.
        var sample = SampleFeatures(features, memberIndices, options.InteriorSampleCount);

        return new SceneTileNode(
            west, south, east, north,
            geometricError,
            depth,
            sample,
            children);
    }

    private static void AddChild(
        List<SceneTileNode> children,
        IReadOnlyList<SceneFeature> features,
        (double Lon, double Lat)[] centroids,
        List<int> members,
        double west,
        double south,
        double east,
        double north,
        int depth,
        double geometricError,
        SceneLodOptions options)
    {
        if (members.Count == 0)
        {
            return;
        }

        children.Add(BuildNode(
            features,
            centroids,
            members.ToArray(),
            west, south, east, north,
            depth,
            geometricError,
            options));
    }

    private static SceneFeature[] MaterializeFeatures(
        IReadOnlyList<SceneFeature> features,
        int[] memberIndices)
    {
        // memberIndices preserves ascending original order by construction
        // (recursion partitions an already-ascending slice into ascending
        // sub-slices), so no sort is needed.
        var result = new SceneFeature[memberIndices.Length];
        for (var i = 0; i < memberIndices.Length; i++)
        {
            result[i] = features[memberIndices[i]];
        }
        return result;
    }

    private static SceneFeature[] SampleFeatures(
        IReadOnlyList<SceneFeature> features,
        int[] memberIndices,
        int sampleCount)
    {
        if (sampleCount <= 0 || memberIndices.Length <= sampleCount)
        {
            return MaterializeFeatures(features, memberIndices);
        }

        // Deterministic uniform stride over the ascending member set. Using a
        // fixed-point accumulator avoids floating-point drift and yields a
        // spatially spread, primary-key-ordered representative subset.
        var sample = new SceneFeature[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var sourceIndex = (int)((long)i * memberIndices.Length / sampleCount);
            sample[i] = features[memberIndices[sourceIndex]];
        }
        return sample;
    }

    private static (double Lon, double Lat) ComputeCentroid(IReadOnlyList<SceneVertex> vertices)
    {
        if (vertices.Count == 0)
        {
            return (0.0, 0.0);
        }
        if (vertices.Count == 1)
        {
            return (vertices[0].Longitude, vertices[0].Latitude);
        }

        // Arithmetic mean of the ring/line vertices. For a closed ring whose
        // first and last vertex coincide we drop the duplicate so the centroid
        // is not biased toward the closing vertex.
        var count = vertices.Count;
        var first = vertices[0];
        var last = vertices[count - 1];
        if (count > 2
            && first.Longitude == last.Longitude
            && first.Latitude == last.Latitude)
        {
            count--;
        }

        var sumLon = 0.0;
        var sumLat = 0.0;
        for (var i = 0; i < count; i++)
        {
            sumLon += vertices[i].Longitude;
            sumLat += vertices[i].Latitude;
        }
        return (sumLon / count, sumLat / count);
    }
}
