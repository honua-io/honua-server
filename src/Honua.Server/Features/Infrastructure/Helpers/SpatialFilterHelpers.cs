// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Features.Infrastructure.Helpers;

/// <summary>
/// Shared helpers for creating spatial filters from bounding box extents.
/// Used by MapServer (export, tile, WMS) and OGC Tiles endpoints.
/// </summary>
internal static class SpatialFilterHelpers
{
    /// <summary>
    /// Creates a spatial intersect filter from a bounding box.
    /// </summary>
    public static SpatialFilter CreateBboxSpatialFilter(
        double minX, double minY, double maxX, double maxY, int srid)
    {
        var wkb = CreateEnvelopeWkb(minX, minY, maxX, maxY);
        return SpatialFilter.Create(wkb, SpatialRelationship.Intersects, srid);
    }

    /// <summary>
    /// Creates a WKB polygon representing a bounding box envelope.
    /// </summary>
    public static byte[] CreateEnvelopeWkb(double minX, double minY, double maxX, double maxY)
    {
        var wkb = new byte[93];
        var offset = 0;

        wkb[offset++] = 1; // little-endian

        BitConverter.TryWriteBytes(wkb.AsSpan(offset), 3); // WKB Polygon
        offset += 4;

        BitConverter.TryWriteBytes(wkb.AsSpan(offset), 1); // 1 ring
        offset += 4;

        BitConverter.TryWriteBytes(wkb.AsSpan(offset), 5); // 5 points
        offset += 4;

        WritePoint(wkb, ref offset, minX, minY);
        WritePoint(wkb, ref offset, maxX, minY);
        WritePoint(wkb, ref offset, maxX, maxY);
        WritePoint(wkb, ref offset, minX, maxY);
        WritePoint(wkb, ref offset, minX, minY);

        return wkb;
    }

    private static void WritePoint(byte[] buffer, ref int offset, double x, double y)
    {
        BitConverter.TryWriteBytes(buffer.AsSpan(offset), x);
        offset += 8;
        BitConverter.TryWriteBytes(buffer.AsSpan(offset), y);
        offset += 8;
    }
}
