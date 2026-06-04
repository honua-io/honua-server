// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Generation;

/// <summary>
/// Thresholds controlling the quadtree LOD partitioning performed by
/// <see cref="SceneQuadtreePartitioner"/> (#1200).
/// </summary>
public sealed record SceneLodOptions
{
    /// <summary>
    /// Maximum number of features a leaf tile may hold before the partitioner
    /// subdivides it. Smaller values produce deeper trees and smaller tile
    /// payloads at the cost of more tile requests.
    /// </summary>
    public int MaxFeaturesPerTile { get; init; } = 2_000;

    /// <summary>
    /// Hard cap on quadtree depth. Bounds the tree even for pathologically
    /// clustered datasets where subdivision never separates features.
    /// </summary>
    public int MaxDepth { get; init; } = 12;

    /// <summary>
    /// Number of representative features each interior node carries as its
    /// coarse LOD (REPLACE refinement). Zero disables interior content (the
    /// tileset then only carries leaf geometry).
    /// </summary>
    public int InteriorSampleCount { get; init; } = 256;

    /// <summary>
    /// Returns the canonical single-tile options used when LOD is disabled:
    /// every feature in one leaf, no interior sampling.
    /// </summary>
    public static SceneLodOptions SingleTile { get; } = new()
    {
        MaxFeaturesPerTile = int.MaxValue,
        MaxDepth = 0,
        InteriorSampleCount = 0
    };
}

/// <summary>
/// A deterministic quadtree of scene tiles produced by
/// <see cref="SceneQuadtreePartitioner"/>. Each node owns a feature subset
/// (the leaf's full membership or an interior node's thinned sample) and a
/// geographic envelope, from which the executor builds GLB content and the
/// <c>tileset.json</c> tree.
/// </summary>
public sealed class SceneTileTree
{
    /// <summary>Creates a tile tree.</summary>
    /// <param name="root">Root node of the hierarchy.</param>
    /// <param name="rootGeometricError">Geometric error declared on the root, in meters.</param>
    public SceneTileTree(SceneTileNode root, double rootGeometricError)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        RootGeometricError = rootGeometricError;
    }

    /// <summary>Root node of the quadtree.</summary>
    public SceneTileNode Root { get; }

    /// <summary>Geometric error declared on the root tile, in meters.</summary>
    public double RootGeometricError { get; }

    /// <summary>
    /// Total number of nodes that carry tile content (every node with at least
    /// one feature). Equals the number of GLB files the executor writes.
    /// </summary>
    public int ContentNodeCount => CountContentNodes(Root);

    /// <summary>Maximum depth reached by any leaf in the tree.</summary>
    public int MaxDepthReached => ComputeMaxDepth(Root);

    private static int CountContentNodes(SceneTileNode node)
    {
        var count = node.Features.Count > 0 ? 1 : 0;
        foreach (var child in node.Children)
        {
            count += CountContentNodes(child);
        }
        return count;
    }

    private static int ComputeMaxDepth(SceneTileNode node)
    {
        var max = node.Depth;
        foreach (var child in node.Children)
        {
            var childMax = ComputeMaxDepth(child);
            if (childMax > max)
            {
                max = childMax;
            }
        }
        return max;
    }
}

/// <summary>
/// A single node in a <see cref="SceneTileTree"/>. Carries the node's
/// geographic envelope (WGS-84 degrees), geometric error, depth, the feature
/// subset rendered into its GLB content, and its ordered child nodes.
/// </summary>
public sealed class SceneTileNode
{
    /// <summary>Creates a tile node.</summary>
    /// <param name="west">Western envelope bound, degrees.</param>
    /// <param name="south">Southern envelope bound, degrees.</param>
    /// <param name="east">Eastern envelope bound, degrees.</param>
    /// <param name="north">Northern envelope bound, degrees.</param>
    /// <param name="geometricError">Geometric error of this node, meters (0 at leaves).</param>
    /// <param name="depth">Zero-based depth (root = 0).</param>
    /// <param name="features">Features rendered into this node's content.</param>
    /// <param name="children">Ordered child nodes.</param>
    public SceneTileNode(
        double west,
        double south,
        double east,
        double north,
        double geometricError,
        int depth,
        IReadOnlyList<SceneFeature> features,
        IReadOnlyList<SceneTileNode> children)
    {
        West = west;
        South = south;
        East = east;
        North = north;
        GeometricError = geometricError;
        Depth = depth;
        Features = features ?? throw new ArgumentNullException(nameof(features));
        Children = children ?? throw new ArgumentNullException(nameof(children));
    }

    /// <summary>Western envelope bound, degrees.</summary>
    public double West { get; }

    /// <summary>Southern envelope bound, degrees.</summary>
    public double South { get; }

    /// <summary>Eastern envelope bound, degrees.</summary>
    public double East { get; }

    /// <summary>Northern envelope bound, degrees.</summary>
    public double North { get; }

    /// <summary>Geometric error of this node, meters. Leaves declare 0.</summary>
    public double GeometricError { get; }

    /// <summary>Zero-based depth in the tree (root = 0).</summary>
    public int Depth { get; }

    /// <summary>Features rendered into this node's GLB content (may be empty).</summary>
    public IReadOnlyList<SceneFeature> Features { get; }

    /// <summary>Ordered child nodes (empty at leaves).</summary>
    public IReadOnlyList<SceneTileNode> Children { get; }

    /// <summary>True when this node is a leaf (no children).</summary>
    public bool IsLeaf => Children.Count == 0;

    /// <summary>Envelope as a [west, south, east, north] degree array.</summary>
    public double[] BoundsDegrees => [West, South, East, North];
}
