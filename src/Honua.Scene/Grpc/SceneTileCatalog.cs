// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Domain = Honua.Core.Features.Scene.Domain;
using Proto = Geospatial.V1;

namespace Honua.Scene.Grpc;

/// <summary>
/// A single tileset tree node paired with the stable identifier and level of
/// detail assigned by <see cref="SceneTileCatalog"/>.
/// </summary>
internal sealed record SceneTileEntry(
    string NodeId,
    Domain.TileNode Node,
    int Lod,
    IReadOnlyList<string> ChildIds);

/// <summary>
/// Pure projection of a 3D Tiles <see cref="Domain.TilesetDocument"/> tree into
/// a flat, addressable node catalog and the canonical gRPC tile messages.
///
/// Node ids are derived deterministically from the node's position in the tree
/// (<c>"0"</c> for the root, <c>"0-0"</c>, <c>"0-1"</c> for its children, and so
/// on), so the same tileset always yields the same ids without persisting them.
/// The level of detail is the node's depth from the root.
/// </summary>
internal static class SceneTileCatalog
{
    private const int Wgs84 = 4326;
    private const double RadiansToDegrees = 180d / Math.PI;

    /// <summary>Flattens the tileset tree into an ordered (root-first) node catalog.</summary>
    public static IReadOnlyList<SceneTileEntry> Build(Domain.TilesetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var entries = new List<SceneTileEntry>();
        Walk(document.Root, "0", lod: 0, entries);
        return entries;
    }

    private static void Walk(Domain.TileNode? node, string nodeId, int lod, List<SceneTileEntry> accumulator)
    {
        if (node is null)
        {
            return;
        }

        var children = node.Children;
        var childIds = new List<string>(children?.Count ?? 0);
        if (children is not null)
        {
            for (var i = 0; i < children.Count; i++)
            {
                childIds.Add($"{nodeId}-{i}");
            }
        }

        accumulator.Add(new SceneTileEntry(nodeId, node, lod, childIds));

        if (children is not null)
        {
            for (var i = 0; i < children.Count; i++)
            {
                Walk(children[i], childIds[i], lod + 1, accumulator);
            }
        }
    }

    /// <summary>Maps a catalog entry to the gRPC <see cref="Proto.TileNode"/> metadata message.</summary>
    public static Proto.TileNode ToProtoNode(SceneTileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var protoNode = new Proto.TileNode
        {
            NodeId = entry.NodeId,
            GeometricError = entry.Node.GeometricError,
            Lod = entry.Lod,
        };
        protoNode.ChildNodeIds.AddRange(entry.ChildIds);

        var boundingVolume = ToBoundingVolume(entry.Node.BoundingVolume);
        if (boundingVolume is not null)
        {
            protoNode.BoundingVolume = boundingVolume;
        }

        return protoNode;
    }

    /// <summary>
    /// Maps a 3D Tiles region bounding volume (west/south/east/north in radians
    /// plus a min/max height in meters) to the gRPC region bounding volume,
    /// converting the horizontal envelope to WGS 84 degrees.
    /// </summary>
    public static Proto.BoundingVolume? ToBoundingVolume(Domain.BoundingVolume? volume)
    {
        var region = volume?.Region;
        if (region is null || region.Length < 6)
        {
            return null;
        }

        return new Proto.BoundingVolume
        {
            Region = new Proto.Extent3D
            {
                Extent = new Proto.Extent
                {
                    Xmin = region[0] * RadiansToDegrees,
                    Ymin = region[1] * RadiansToDegrees,
                    Xmax = region[2] * RadiansToDegrees,
                    Ymax = region[3] * RadiansToDegrees,
                    SpatialReference = new Proto.SpatialReference { Wkid = Wgs84, LatestWkid = Wgs84 },
                },
                MinHeight = region[4],
                MaxHeight = region[5],
            },
        };
    }

    /// <summary>Infers the gRPC tile content encoding from a content uri's extension.</summary>
    public static Proto.TileContentType ContentTypeFromUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return Proto.TileContentType.Unspecified;
        }

        return Path.GetExtension(uri).ToLowerInvariant() switch
        {
            ".glb" => Proto.TileContentType.Glb,
            ".b3dm" => Proto.TileContentType.B3Dm,
            ".i3dm" => Proto.TileContentType.I3Dm,
            ".pnts" => Proto.TileContentType.Pnts,
            _ => Proto.TileContentType.Unspecified,
        };
    }

    /// <summary>
    /// Tests whether a tile node's horizontal region intersects the requested
    /// gRPC extent. Nodes without a region bound (or requests without an
    /// extent) are treated as matches so unbounded queries stream everything.
    /// </summary>
    public static bool IntersectsExtent(Domain.TileNode node, Proto.Extent3D? requestExtent)
    {
        ArgumentNullException.ThrowIfNull(node);

        var filter = requestExtent?.Extent;
        if (filter is null)
        {
            return true;
        }

        var region = node.BoundingVolume?.Region;
        if (region is null || region.Length < 4)
        {
            return true;
        }

        var nodeXMin = region[0] * RadiansToDegrees;
        var nodeYMin = region[1] * RadiansToDegrees;
        var nodeXMax = region[2] * RadiansToDegrees;
        var nodeYMax = region[3] * RadiansToDegrees;

        return nodeXMin <= filter.Xmax
            && nodeXMax >= filter.Xmin
            && nodeYMin <= filter.Ymax
            && nodeYMax >= filter.Ymin;
    }
}
