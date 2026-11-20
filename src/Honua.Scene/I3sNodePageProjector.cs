// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Generation;
using Honua.Scene.Grpc;
using Domain = Honua.Core.Features.Scene.Domain;

namespace Honua.Scene;

public static class I3sNodePageProjector
{
    public const int NodesPerPage = 64;

    public const string LodSelectionMetricType = "maxScreenThresholdSQ";

    private const double DegreesToRadians = Math.PI / 180d;
    private const double ServingScreenSpaceErrorPixels = 16d;
    private const double MinimumEffectiveGeometricErrorMeters = 1e-3d;

    public static IReadOnlyList<I3sNodePageDocument> BuildPages(Domain.TilesetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var entries = SceneTileCatalog.Build(document);
        if (entries.Count == 0)
        {
            return [];
        }

        // Map each node id to its global index so parent/child references resolve
        // to the flattened array positions an I3S client traverses.
        var indexById = new Dictionary<string, int>(entries.Count, StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            indexById[entries[i].NodeId] = i;
        }

        var nodes = new List<I3sNodePageEntry>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            nodes.Add(ProjectNode(entries[i], i, indexById));
        }

        var pageCount = (entries.Count + NodesPerPage - 1) / NodesPerPage;
        var pages = new List<I3sNodePageDocument>(pageCount);
        for (var page = 0; page < pageCount; page++)
        {
            var start = page * NodesPerPage;
            var end = Math.Min(start + NodesPerPage, nodes.Count);
            pages.Add(new I3sNodePageDocument
            {
                Nodes = nodes.GetRange(start, end - start),
            });
        }

        return pages;
    }

    public static int PageCount(Domain.TilesetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var nodeCount = SceneTileCatalog.Build(document).Count;
        return (nodeCount + NodesPerPage - 1) / NodesPerPage;
    }

    private static I3sNodePageEntry ProjectNode(
        SceneTileEntry entry,
        int globalIndex,
        Dictionary<string, int> indexById)
    {
        var children = new List<int>(entry.ChildIds.Count);
        var childIdIndex = 0;
        while (childIdIndex < entry.ChildIds.Count)
        {
            if (indexById.TryGetValue(entry.ChildIds[childIdIndex], out var childIndex))
            {
                children.Add(childIndex);
            }

            childIdIndex++;
        }

        var obb = ProjectObb(entry.Node.BoundingVolume);
        var node = new I3sNodePageEntry
        {
            Index = globalIndex,
            // The root selects unconditionally (threshold 0); descendants refine
            // according to I3S maxScreenThresholdSQ (projected bounding-sphere
            // screen pixel area π * (r * SSE / ε)^2).
            LodThreshold = entry.Lod == 0 ? 0d : ComputeMaxScreenThresholdSq(entry.Node.GeometricError, obb),
            ParentIndex = entry.Lod == 0 ? null : FindParentIndex(entry.NodeId, indexById),
            Children = children.Count > 0 ? children : null,
            Obb = obb,
        };

        // Only content-bearing nodes advertise a fetchable mesh. The global index
        // is the stable per-node resource id substituted into node-resource URLs.
        if (entry.Node.Content is { Uri.Length: > 0 })
        {
            node.Mesh = new I3sNodeMesh
            {
                Geometry = new I3sNodeResourceReference { Definition = 0, Resource = globalIndex },
                Attribute = new I3sNodeResourceReference { Definition = 0, Resource = globalIndex },
                Material = new I3sNodeResourceReference { Definition = 0, Resource = globalIndex },
            };
        }

        return node;
    }

    private static int? FindParentIndex(string nodeId, Dictionary<string, int> indexById)
    {
        var lastDash = nodeId.LastIndexOf('-');
        if (lastDash < 0)
        {
            return null;
        }

        var parentId = nodeId[..lastDash];
        return indexById.TryGetValue(parentId, out var parentIndex) ? parentIndex : null;
    }

    private static I3sOrientedBoundingBox? ProjectObb(Domain.BoundingVolume? volume)
    {
        var region = volume?.Region;
        if (region is not { Length: >= 6 })
        {
            return null;
        }

        var west = region[0];
        var south = region[1];
        var east = region[2];
        var north = region[3];
        var minHeight = region[4];
        var maxHeight = region[5];

        var centerLonRad = (west + east) / 2d;
        var centerLatRad = (south + north) / 2d;
        var centerLonDeg = centerLonRad / DegreesToRadians;
        var centerLatDeg = centerLatRad / DegreesToRadians;
        var centerHeight = (minHeight + maxHeight) / 2d;

        // Half-sizes in metres. The east-west span shrinks with latitude
        // (cos(lat)); the north-south span uses the meridian arc. These are the
        // standard small-angle metric extents used to size an MBS/OBB from a
        // geographic envelope.
        var halfHeightMetres = (north - south) / 2d * EcefCoordinateTransform.WgsSemiMajorAxis;
        var halfWidthMetres = (east - west) / 2d
            * EcefCoordinateTransform.WgsSemiMajorAxis
            * Math.Cos(centerLatRad);
        var halfElevationMetres = (maxHeight - minHeight) / 2d;

        return new I3sOrientedBoundingBox
        {
            Center = [centerLonDeg, centerLatDeg, centerHeight],
            HalfSize =
            [
                Math.Abs(halfWidthMetres),
                Math.Abs(halfHeightMetres),
                Math.Abs(halfElevationMetres),
            ],
            Quaternion = ComputeEnuToEcefQuaternion(centerLonRad, centerLatRad),
        };
    }

    private static double ComputeMaxScreenThresholdSq(double geometricError, I3sOrientedBoundingBox? obb)
    {
        var radiusSquared = 0d;
        if (obb is { HalfSize.Length: >= 3 })
        {
            var hx = obb.HalfSize[0];
            var hy = obb.HalfSize[1];
            var hz = obb.HalfSize[2];
            radiusSquared = (hx * hx) + (hy * hy) + (hz * hz);
        }

        if (radiusSquared <= 0d || !double.IsFinite(radiusSquared))
        {
            radiusSquared = 1d;
        }

        double effectiveError;
        if (geometricError > 0d && double.IsFinite(geometricError))
        {
            effectiveError = geometricError;
        }
        else
        {
            // Terminal 3D Tiles leaf nodes often carry geometricError == 0.
            // Derive a small positive refinement error from the node's extent
            // so the leaf still advertises a positive maxScreenThresholdSQ.
            var radius = Math.Sqrt(radiusSquared);
            effectiveError = Math.Max(MinimumEffectiveGeometricErrorMeters, Math.Min(1d, radius * 0.01d));
        }

        var ratio = ServingScreenSpaceErrorPixels / effectiveError;
        return Math.PI * radiusSquared * ratio * ratio;
    }

    public static double[] ComputeEnuToEcefQuaternion(double longitudeRadians, double latitudeRadians)
    {
        // Local ENU axes (+X East, +Y North, +Z Up) rotate into ECEF via:
        //   R = R_z(lon + π/2) * R_x(π/2 - lat)
        // Compose the two half-angle axis rotations q = q_z(β) * q_x(α) where
        // α = (π/2 - lat) and β = (lon + π/2), returning [x, y, z, w].
        var halfAlpha = (Math.PI * 0.5d - latitudeRadians) * 0.5d;
        var halfBeta = (longitudeRadians + Math.PI * 0.5d) * 0.5d;

        var sinAlpha = Math.Sin(halfAlpha);
        var cosAlpha = Math.Cos(halfAlpha);
        var sinBeta = Math.Sin(halfBeta);
        var cosBeta = Math.Cos(halfBeta);

        var qx = cosBeta * sinAlpha;
        var qy = sinBeta * sinAlpha;
        var qz = sinBeta * cosAlpha;
        var qw = cosBeta * cosAlpha;

        return [qx, qy, qz, qw];
    }
}
