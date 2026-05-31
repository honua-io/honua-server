// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;

namespace Honua.Import.Features.I3sImport;

/// <summary>
/// Maps an I3S NodePage hierarchy to an OGC 3D Tiles 1.1
/// <see cref="TilesetDocument"/> tree.
/// </summary>
/// <remarks>
/// <para>
/// Each I3S node becomes one <see cref="TileNode"/>. Bounding volumes are
/// emitted as ECEF bounding spheres (<see cref="BoundingVolume.Sphere"/>) so a
/// CesiumJS client can position and frustum-cull the tile without recomputing
/// the centre in geographic space. Refinement is set to <c>REPLACE</c> to
/// honor the I3S LOD semantics where higher-detail children replace their
/// parent during traversal.
/// </para>
/// <para>
/// Geometric error is approximated as <c>diameter / 2^depth</c>: conservative,
/// safe for first-render, and replaceable with a screen-space derivation once
/// the conversion pipeline tracks LOD thresholds end-to-end.
/// </para>
/// </remarks>
internal sealed class I3sTilesetBuilder
{
    private readonly I3sNodeTreeReader _tree;
    private readonly Dictionary<int, string> _nodeContentUrisByIndex;
    private readonly double _rootDiameterMeters;

    /// <summary>
    /// Initializes a new builder.
    /// </summary>
    /// <param name="tree">Node tree reader (already bound to the open .slpk).</param>
    /// <param name="nodeContentUrisByIndex">
    /// Map of I3S node index → relative GLB URI (omitted entries become content-less
    /// "parent" tiles that simply route to their children).
    /// </param>
    /// <param name="rootDiameterMeters">
    /// Root MBS diameter in meters, used to seed the geometric-error progression.
    /// </param>
    public I3sTilesetBuilder(
        I3sNodeTreeReader tree,
        Dictionary<int, string> nodeContentUrisByIndex,
        double rootDiameterMeters)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(nodeContentUrisByIndex);

        _tree = tree;
        _nodeContentUrisByIndex = nodeContentUrisByIndex;
        _rootDiameterMeters = Math.Max(1.0, rootDiameterMeters);
    }

    /// <summary>
    /// Builds the tileset document.
    /// </summary>
    /// <param name="generatorTag">Generator label written into <c>asset.generator</c>.</param>
    public TilesetDocument Build(string? generatorTag)
    {
        var root = _tree.GetRoot();
        var rootTile = BuildTileNode(root, depth: 0);
        var rootGeometricError = _rootDiameterMeters;

        return new TilesetDocument
        {
            Asset = new TilesetAsset
            {
                Version = "1.1",
                Generator = string.IsNullOrEmpty(generatorTag) ? null : generatorTag
            },
            GeometricError = rootGeometricError,
            Root = rootTile
        };
    }

    private TileNode BuildTileNode(I3sNodePageEntry node, int depth)
    {
        var boundingVolume = BuildBoundingVolume(node);
        var depthFactor = Math.Pow(2.0, depth);
        var geometricError = _rootDiameterMeters / Math.Max(1.0, depthFactor);
        // Leaf nodes (no children) must declare geometricError=0 per the OGC spec.
        if (node.Children is null || node.Children.Length == 0)
        {
            geometricError = 0.0;
        }

        TileContent? content = null;
        if (_nodeContentUrisByIndex.TryGetValue(node.Index, out var contentUri))
        {
            content = new TileContent { Uri = contentUri };
        }

        IReadOnlyList<TileNode>? children = null;
        if (node.Children is not null && node.Children.Length > 0)
        {
            var childList = new List<TileNode>(node.Children.Length);
            foreach (var childIndex in node.Children)
            {
                var childEntry = _tree.GetNode(childIndex);
                childList.Add(BuildTileNode(childEntry, depth + 1));
            }
            children = childList;
        }

        return new TileNode
        {
            BoundingVolume = boundingVolume,
            GeometricError = geometricError,
            Refine = "REPLACE",
            Content = content,
            Children = children
        };
    }

    private static BoundingVolume BuildBoundingVolume(I3sNodePageEntry node)
    {
        if (node.Mbs is { Length: 4 } mbs)
        {
            var (x, y, z) = EcefCoordinateTransform.ToEcef(mbs[0], mbs[1], mbs[2]);
            return new BoundingVolume
            {
                Sphere = [x, y, z, mbs[3]]
            };
        }

        if (node.Obb is { Center: { Length: 3 } center, HalfSize: { Length: 3 } halfSize })
        {
            var (x, y, z) = EcefCoordinateTransform.ToEcef(center[0], center[1], center[2]);
            // OBB without rotation is good enough for the initial slice — emit an
            // axis-aligned box in ECEF using the half sizes directly. A full
            // quaternion-rotated box requires basis-vector math that is deferred
            // until the converter handles per-node OBB orientation.
            return new BoundingVolume
            {
                Box =
                [
                    x, y, z,
                    halfSize[0], 0.0, 0.0,
                    0.0, halfSize[1], 0.0,
                    0.0, 0.0, halfSize[2]
                ]
            };
        }

        throw new InvalidOperationException(
            $"I3S node {node.Index} declared no bounding volume (mbs/obb both missing).");
    }
}
