// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Raster;

internal static class RasterAnalysisGeometry
{
    internal static byte[] BuildLineWkb(double startLon, double startLat, double endLon, double endLat)
    {
        // Byte order (1) + type (4) + numPoints (4) + 2 * (x,y as float8) = 41 bytes.
        var buffer = new byte[41];
        buffer[0] = 1; // little-endian
        buffer[1] = 0x02; // geometry type = 2 (LineString)
        buffer[5] = 0x02; // numPoints = 2
        BitConverter.TryWriteBytes(buffer.AsSpan(9, 8), startLon);
        BitConverter.TryWriteBytes(buffer.AsSpan(17, 8), startLat);
        BitConverter.TryWriteBytes(buffer.AsSpan(25, 8), endLon);
        BitConverter.TryWriteBytes(buffer.AsSpan(33, 8), endLat);
        return buffer;
    }

    internal static (double Longitude, double Latitude) DestinationPoint(
        double startLon,
        double startLat,
        double azimuthDegrees,
        double distanceMeters)
    {
        var angular = distanceMeters / SpatialConstants.EarthRadius;
        var theta = DegToRad(azimuthDegrees);
        var phi1 = DegToRad(startLat);
        var lambda1 = DegToRad(startLon);

        var sinPhi2 = Math.Sin(phi1) * Math.Cos(angular)
            + Math.Cos(phi1) * Math.Sin(angular) * Math.Cos(theta);
        var phi2 = Math.Asin(Math.Clamp(sinPhi2, -1.0, 1.0));
        var lambda2 = lambda1 + Math.Atan2(
            Math.Sin(theta) * Math.Sin(angular) * Math.Cos(phi1),
            Math.Cos(angular) - Math.Sin(phi1) * sinPhi2);

        var lon = RadToDeg(lambda2);
        lon = ((lon + 540.0) % 360.0) - 180.0;
        return (lon, RadToDeg(phi2));
    }

    private static double DegToRad(double degrees) => degrees * Math.PI / 180.0;

    private static double RadToDeg(double radians) => radians * 180.0 / Math.PI;
}
