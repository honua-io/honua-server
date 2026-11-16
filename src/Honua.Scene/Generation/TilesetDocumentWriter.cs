// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Generation;

public static class TilesetDocumentWriter
{
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

        // 3D Tiles 1.1 requires a tile's boundingVolume to spatially enclose
        // every descendant's bounding volume. The lon/lat bounds come from the
        // node envelope (which already encloses its children), but the vertical
        // extent must too. Derive it from the union of this node's own content
        // and every content-bearing descendant rather than the node's own
        // emitted [min,max]: a content-bearing interior node's height comes from
        // a thinned representative sample of its subtree (SampleFeatures strides
        // by key order, not height), so the sample can exclude the leaf carrying
        // the subtree's extreme height — leaving the node's own extent narrower
        // than its descendants. A content-less interior node (interior sampling
        // disabled) has no own extent at all. Either way the node's own [min,max]
        // can fail to enclose descendants and let a conformant client cull
        // visible geometry, so always union over the whole subtree.
        double minHeight;
        double maxHeight;
        if (TryGetDescendantHeightExtent(node, content, out var subtreeMin, out var subtreeMax))
        {
            minHeight = subtreeMin;
            maxHeight = subtreeMax;
        }
        else
        {
            // No content anywhere in this subtree; a degenerate [0,0] slab is
            // the only meaningful vertical extent and encloses nothing real.
            minHeight = 0.0;
            maxHeight = 0.0;
        }

        var region = new[]
        {
            node.West * Math.PI / 180.0,
            node.South * Math.PI / 180.0,
            node.East * Math.PI / 180.0,
            node.North * Math.PI / 180.0,
            minHeight,
            maxHeight
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

    private static bool TryGetDescendantHeightExtent(
        SceneTileNode node,
        IReadOnlyDictionary<SceneTileNode, SceneTileContent> content,
        out double minHeight,
        out double maxHeight)
    {
        var found = false;
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;

        if (content.TryGetValue(node, out var nodeContent))
        {
            found = true;
            min = nodeContent.MinHeightMeters;
            max = nodeContent.MaxHeightMeters;
        }

        var childIndex = 0;
        while (childIndex < node.Children.Count)
        {
            if (TryGetDescendantHeightExtent(
                    node.Children[childIndex],
                    content,
                    out var childMin,
                    out var childMax))
            {
                found = true;
                if (childMin < min) min = childMin;
                if (childMax > max) max = childMax;
            }

            childIndex++;
        }

        minHeight = found ? min : 0.0;
        maxHeight = found ? max : 0.0;
        return found;
    }

    public static byte[] Serialize(TilesetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.SerializeToUtf8Bytes(document, TilesetJsonContext.Default.TilesetDocument);
    }
}

public readonly record struct SceneTileContent(string Uri, double MinHeightMeters, double MaxHeightMeters);
