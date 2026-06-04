// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Generation;

/// <summary>
/// Deterministic writer for the v1 3D Tiles <c>tileset.json</c> document.
/// </summary>
/// <remarks>
/// Uses the source-generated <see cref="TilesetJsonContext"/> for AOT-safe
/// serialization. Properties are written in declaration order; the document
/// model contains no dictionaries so the resulting byte sequence is stable
/// across runs given identical input.
/// </remarks>
public static class TilesetDocumentWriter
{
    /// <summary>
    /// Builds an in-memory tileset document from a single-tile generation
    /// result.
    /// </summary>
    /// <param name="boundingRegionDegrees">Bounding region in WGS-84 degrees [west, south, east, north].</param>
    /// <param name="minHeightMeters">Minimum height across geometry, in meters.</param>
    /// <param name="maxHeightMeters">Maximum height across geometry, in meters.</param>
    /// <param name="geometricError">Geometric error in meters.</param>
    /// <param name="tileContentUris">Ordered relative URIs of child tile content (e.g. <c>tile_0000.glb</c>).</param>
    /// <param name="generatorTag">Optional generator label written into the asset block.</param>
    /// <param name="styleReference">
    /// Optional reference to the emitted style-metadata contract sidecar. When
    /// supplied it is advertised under the root <c>extras.honua_style</c> block
    /// so a client can discover the attribute-driven symbology spec.
    /// </param>
    public static TilesetDocument Build(
        double[] boundingRegionDegrees,
        double minHeightMeters,
        double maxHeightMeters,
        double geometricError,
        IReadOnlyList<string> tileContentUris,
        string? generatorTag = null,
        TilesetStyleReference? styleReference = null)
    {
        ArgumentNullException.ThrowIfNull(boundingRegionDegrees);
        ArgumentNullException.ThrowIfNull(tileContentUris);
        if (boundingRegionDegrees.Length != 4)
        {
            throw new ArgumentException(
                "boundingRegionDegrees must have exactly 4 elements.",
                nameof(boundingRegionDegrees));
        }

        var region = new[]
        {
            boundingRegionDegrees[0] * Math.PI / 180.0,
            boundingRegionDegrees[1] * Math.PI / 180.0,
            boundingRegionDegrees[2] * Math.PI / 180.0,
            boundingRegionDegrees[3] * Math.PI / 180.0,
            minHeightMeters,
            maxHeightMeters
        };

        var children = new List<TileNode>(tileContentUris.Count);
        for (var i = 0; i < tileContentUris.Count; i++)
        {
            children.Add(new TileNode
            {
                BoundingVolume = new BoundingVolume { Region = region },
                GeometricError = 0.0,
                Refine = "ADD",
                Content = new TileContent { Uri = tileContentUris[i] }
            });
        }

        return new TilesetDocument
        {
            Asset = new TilesetAsset
            {
                Version = "1.1",
                Generator = string.IsNullOrEmpty(generatorTag) ? null : generatorTag
            },
            GeometricError = geometricError,
            Root = new TileNode
            {
                BoundingVolume = new BoundingVolume { Region = region },
                GeometricError = geometricError,
                Refine = "ADD",
                Children = children
            },
            Extras = styleReference is null
                ? null
                : new TilesetExtras { Style = styleReference }
        };
    }

    /// <summary>
    /// Builds a multi-level tileset document from a quadtree LOD partition
    /// (#1200). Every <see cref="SceneTileNode"/> that carries content becomes a
    /// <see cref="TileNode"/> with its own bounding region and tile content; the
    /// tree refinement is "REPLACE" so a client swaps a coarse interior tile for
    /// its finer children as the camera approaches.
    /// </summary>
    /// <param name="tree">The partition produced by <see cref="SceneQuadtreePartitioner"/>.</param>
    /// <param name="content">
    /// Maps a content-bearing node to its emitted content (relative GLB uri plus
    /// the node's vertical extent in meters). Nodes with no features are skipped.
    /// </param>
    /// <param name="generatorTag">Optional generator label written into the asset block.</param>
    /// <param name="styleReference">Optional style-metadata contract reference advertised under <c>extras</c>.</param>
    public static TilesetDocument BuildTree(
        SceneTileTree tree,
        IReadOnlyDictionary<SceneTileNode, SceneTileContent> content,
        string? generatorTag = null,
        TilesetStyleReference? styleReference = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(content);

        var root = BuildTileNode(tree.Root, content);

        return new TilesetDocument
        {
            Asset = new TilesetAsset
            {
                Version = "1.1",
                Generator = string.IsNullOrEmpty(generatorTag) ? null : generatorTag
            },
            // The tileset-level geometricError and the root tile's geometricError
            // describe the same root LOD budget and clients may use either, so they
            // must agree. The partitioner always assigns the floored, positive root
            // geometric error to the root node — even when the root is also a leaf
            // (SceneQuadtreePartitioner.LeafGeometricError) — so root.GeometricError
            // equals tree.RootGeometricError here and the two stay consistent.
            GeometricError = tree.RootGeometricError,
            Root = root,
            Extras = styleReference is null
                ? null
                : new TilesetExtras { Style = styleReference }
        };
    }

    private static TileNode BuildTileNode(
        SceneTileNode node,
        IReadOnlyDictionary<SceneTileNode, SceneTileContent> content)
    {
        var hasContent = content.TryGetValue(node, out var nodeContent);

        var region = new[]
        {
            node.West * Math.PI / 180.0,
            node.South * Math.PI / 180.0,
            node.East * Math.PI / 180.0,
            node.North * Math.PI / 180.0,
            hasContent ? nodeContent.MinHeightMeters : 0.0,
            hasContent ? nodeContent.MaxHeightMeters : 0.0
        };

        List<TileNode>? children = null;
        if (node.Children.Count > 0)
        {
            children = new List<TileNode>(node.Children.Count);
            foreach (var child in node.Children)
            {
                children.Add(BuildTileNode(child, content));
            }
        }

        return new TileNode
        {
            BoundingVolume = new BoundingVolume { Region = region },
            GeometricError = node.GeometricError,
            // REPLACE so a parent's coarse LOD is swapped for its children's
            // finer geometry as the client refines toward the leaves.
            Refine = "REPLACE",
            Content = hasContent ? new TileContent { Uri = nodeContent.Uri } : null,
            Children = children
        };
    }

    /// <summary>
    /// Serializes the tileset document to a deterministic UTF-8 byte sequence.
    /// </summary>
    public static byte[] Serialize(TilesetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.SerializeToUtf8Bytes(document, TilesetJsonContext.Default.TilesetDocument);
    }
}

/// <summary>
/// Content emitted for a single quadtree tile node: the relative GLB uri plus
/// the node's vertical extent (meters) used to size its bounding region.
/// </summary>
/// <param name="Uri">Relative content uri (e.g. <c>tile_0003.glb</c>).</param>
/// <param name="MinHeightMeters">Minimum vertex height in the node, meters.</param>
/// <param name="MaxHeightMeters">Maximum vertex height in the node, meters.</param>
public readonly record struct SceneTileContent(string Uri, double MinHeightMeters, double MaxHeightMeters);
