// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.FeatureServer.Models;

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Shared helpers for GeoServices geometry conversions.
/// </summary>
internal static class GeoServicesGeometryConverter
{
    /// <summary>
    /// Converts WKB geometry to GeoServices format.
    /// </summary>
    public static GeoServicesGeometry? ConvertWkbToGeoServicesGeometry(byte[]? wkbGeometry)
    {
        if (wkbGeometry == null || wkbGeometry.Length < 21)
            return null;

        // Detect endianness (1 = little-endian, 0 = big-endian)
        bool isLittleEndian = wkbGeometry[0] == 1;
        if (!isLittleEndian && wkbGeometry[0] != 0)
            return null; // Invalid endianness marker

        // Read geometry type with proper endianness
        uint geometryType = isLittleEndian
            ? BitConverter.ToUInt32(wkbGeometry, 1)
            : BitConverter.ToUInt32([.. wkbGeometry.AsSpan(1, 4).ToArray().Reverse()], 0);

        // Only support point geometries for now
        if (geometryType != 1)
        {
            // TODO: Add support for LineString (2), Polygon (3), MultiPoint (4), etc.
            return null;
        }

        // Read coordinates with proper endianness
        double x, y;
        if (isLittleEndian)
        {
            x = BitConverter.ToDouble(wkbGeometry, 5);  // X coordinate at offset 5
            y = BitConverter.ToDouble(wkbGeometry, 13); // Y coordinate at offset 13
        }
        else
        {
            byte[] xBytes = [.. wkbGeometry.AsSpan(5, 8).ToArray().Reverse()];
            byte[] yBytes = [.. wkbGeometry.AsSpan(13, 8).ToArray().Reverse()];
            x = BitConverter.ToDouble(xBytes, 0);
            y = BitConverter.ToDouble(yBytes, 0);
        }

        // TODO: Extract actual SRID from WKB instead of defaulting to 4326
        // For now, use Web Mercator (3857) if coordinates suggest projected data, otherwise WGS84 (4326)
        int srid = (Math.Abs(x) > 180 || Math.Abs(y) > 90) ? 3857 : 4326;

        return new GeoServicesGeometry
        {
            X = x,
            Y = y,
            SpatialReference = new GeoServicesSpatialReference { Wkid = srid }
        };
    }

    /// <summary>
    /// Converts GeoServices point geometry to WKB.
    /// </summary>
    public static byte[] ConvertGeoServicesGeometryToWkb(GeoServicesGeometry geometry)
    {
        if (geometry == null)
            throw new ArgumentNullException(nameof(geometry));

        return ConvertPointToWkb(geometry.X, geometry.Y);
    }

    private static byte[] ConvertPointToWkb(double x, double y)
    {
        // Create WKB for a POINT geometry (little-endian format)
        // WKB format: [endian][type][x][y]
        byte[] wkbBytes = new byte[21]; // 1 + 4 + 8 + 8 bytes
        wkbBytes[0] = 1; // Little-endian
        BitConverter.GetBytes((uint)1).CopyTo(wkbBytes, 1); // POINT type
        BitConverter.GetBytes(x).CopyTo(wkbBytes, 5); // X coordinate
        BitConverter.GetBytes(y).CopyTo(wkbBytes, 13); // Y coordinate

        return wkbBytes;
    }
}
