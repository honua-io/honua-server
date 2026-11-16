// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Domain = Honua.Core.Features.Scene.Domain;
using Proto = Geospatial.V1;

namespace Honua.Scene.Grpc;

internal sealed record SceneTileEntry(
    string NodeId,
    Domain.TileNode Node,
    int Lod,
    IReadOnlyList<string> ChildIds);

internal static class SceneTileCatalog
{
    private const int Wgs84 = 4326;
    private const double RadiansToDegrees = 180d / Math.PI;

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

    private readonly record struct DecodedRegion(
        double XMin,
        double YMin,
        double XMax,
        double YMax,
        bool HasHeight,
        double MinHeight,
        double MaxHeight);

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

        return EnvelopesIntersect(
            decoded.XMin, decoded.YMin, decoded.XMax, decoded.YMax,
            filter.Xmin, filter.Ymin, filter.Xmax, filter.Ymax);
    }

    public static bool EnvelopesIntersect(
        double aXmin, double aYmin, double aXmax, double aYmax,
        double bXmin, double bYmin, double bXmax, double bYmax)
        => aXmin <= bXmax
            && aXmax >= bXmin
            && aYmin <= bYmax
            && aYmax >= bYmin;
}
