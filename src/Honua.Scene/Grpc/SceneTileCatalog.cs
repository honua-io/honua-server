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
    /// <remarks>
    /// The proto <c>BoundingVolume</c> is a oneof of region/box/sphere (mirroring
    /// the 3D Tiles spec), but the Honua domain model only ever carries a
    /// <c>region</c> (<see cref="Domain.BoundingVolume.Region"/>; there is no box
    /// or sphere field), and every server-generated tileset emits region volumes.
    /// Box/sphere therefore cannot occur on this path, so only the region arm of
    /// the oneof is populated. A missing or short (&lt; 6 element) region maps to
    /// <see langword="null"/> rather than a degenerate all-zero region, so a node
    /// without a real bound advertises no bounding volume instead of a bogus one
    /// at the equator/prime-meridian origin.
    /// </remarks>
    public static Proto.BoundingVolume? ToBoundingVolume(Domain.BoundingVolume? volume)
    {
        if (!TryDecodeRegion(volume?.Region, out var decoded) || !decoded.HasHeight)
        {
            return null;
        }

        return new Proto.BoundingVolume
        {
            Region = new Proto.Extent3D
            {
                Extent = new Proto.Extent
                {
                    Xmin = decoded.XMin,
                    Ymin = decoded.YMin,
                    Xmax = decoded.XMax,
                    Ymax = decoded.YMax,
                    SpatialReference = new Proto.SpatialReference { Wkid = Wgs84, LatestWkid = Wgs84 },
                },
                MinHeight = decoded.MinHeight,
                MaxHeight = decoded.MaxHeight,
            },
        };
    }

    /// <summary>
    /// Horizontal envelope (degrees) and optional vertical range (metres)
    /// decoded from a 3D Tiles region bounding volume.
    /// </summary>
    private readonly record struct DecodedRegion(
        double XMin,
        double YMin,
        double XMax,
        double YMax,
        bool HasHeight,
        double MinHeight,
        double MaxHeight);

    /// <summary>
    /// Decodes a 3D Tiles region array (<c>[west, south, east, north]</c> in
    /// radians, plus an optional <c>[minHeight, maxHeight]</c> in metres) into a
    /// WGS-84-degree envelope. Returns <see langword="false"/> for a missing or
    /// short (&lt; 4 element) region so callers can treat it as "no bound".
    /// <see cref="DecodedRegion.HasHeight"/> is set only when the array carries
    /// the full 6 elements. Single shared decode used by every region-consuming
    /// site so the radians-to-degrees conversion stays identical.
    /// </summary>
    private static bool TryDecodeRegion(double[]? region, out DecodedRegion decoded)
    {
        if (region is null || region.Length < 4)
        {
            decoded = default;
            return false;
        }

        var hasHeight = region.Length >= 6;
        decoded = new DecodedRegion(
            XMin: region[0] * RadiansToDegrees,
            YMin: region[1] * RadiansToDegrees,
            XMax: region[2] * RadiansToDegrees,
            YMax: region[3] * RadiansToDegrees,
            HasHeight: hasHeight,
            MinHeight: hasHeight ? region[4] : 0d,
            MaxHeight: hasHeight ? region[5] : 0d);
        return true;
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

        if (!TryDecodeRegion(node.BoundingVolume?.Region, out var decoded))
        {
            return true;
        }

        return decoded.XMin <= filter.Xmax
            && decoded.XMax >= filter.Xmin
            && decoded.YMin <= filter.Ymax
            && decoded.YMax >= filter.Ymin;
    }
}
