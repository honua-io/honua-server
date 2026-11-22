// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Scene.Terrain;

internal readonly record struct EcefPoint(double X, double Y, double Z);

public static class QuantizedMeshEllipsoid
{
    public const double SemiMajorAxis = 6378137.0;

    private const double Flattening = 1.0 / 298.257223563;

    public const double SemiMinorAxis = SemiMajorAxis * (1.0 - Flattening);

    private const double EccentricitySquared = (2.0 * Flattening) - (Flattening * Flattening);

    private const double DegreesToRadians = Math.PI / 180.0;

    internal static EcefPoint GeodeticToEcef(double longitudeDegrees, double latitudeDegrees, double heightMeters)
    {
        var lon = longitudeDegrees * DegreesToRadians;
        var lat = latitudeDegrees * DegreesToRadians;
        var sinLat = Math.Sin(lat);
        var cosLat = Math.Cos(lat);
        var primeVerticalRadius = SemiMajorAxis / Math.Sqrt(1.0 - (EccentricitySquared * sinLat * sinLat));

        var x = (primeVerticalRadius + heightMeters) * cosLat * Math.Cos(lon);
        var y = (primeVerticalRadius + heightMeters) * cosLat * Math.Sin(lon);
        var z = ((primeVerticalRadius * (1.0 - EccentricitySquared)) + heightMeters) * sinLat;
        return new EcefPoint(x, y, z);
    }

    internal static (EcefPoint Center, double Radius) ComputeBoundingSphere(ReadOnlySpan<EcefPoint> positions)
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var maxZ = double.NegativeInfinity;

        for (var i = 0; i < positions.Length; i++)
        {
            var p = positions[i];
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            minZ = Math.Min(minZ, p.Z);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
            maxZ = Math.Max(maxZ, p.Z);
        }

        var center = new EcefPoint((minX + maxX) * 0.5, (minY + maxY) * 0.5, (minZ + maxZ) * 0.5);
        var maxDistanceSquared = 0.0;
        for (var i = 0; i < positions.Length; i++)
        {
            var dx = positions[i].X - center.X;
            var dy = positions[i].Y - center.Y;
            var dz = positions[i].Z - center.Z;
            maxDistanceSquared = Math.Max(maxDistanceSquared, (dx * dx) + (dy * dy) + (dz * dz));
        }

        return (center, Math.Sqrt(maxDistanceSquared));
    }

    internal static EcefPoint ComputeHorizonOcclusionPoint(ReadOnlySpan<EcefPoint> positions, EcefPoint center)
    {
        var direction = ScaleToUnitEllipsoid(center);
        var directionLength = Length(direction);
        if (directionLength <= 1e-12)
        {
            return default;
        }

        var unitDirection = new EcefPoint(
            direction.X / directionLength,
            direction.Y / directionLength,
            direction.Z / directionLength);
        var maxMagnitude = 0.0;

        for (var i = 0; i < positions.Length; i++)
        {
            var magnitude = ComputePointOcclusionMagnitude(ScaleToUnitEllipsoid(positions[i]), unitDirection);
            if (!double.IsFinite(magnitude))
            {
                return default;
            }

            maxMagnitude = Math.Max(maxMagnitude, magnitude);
        }

        return maxMagnitude <= 0.0
            ? default
            : new EcefPoint(unitDirection.X * maxMagnitude, unitDirection.Y * maxMagnitude, unitDirection.Z * maxMagnitude);
    }

    internal static byte[] ComputeOctEncodedVertexNormals(ReadOnlySpan<EcefPoint> positions, ReadOnlySpan<uint> indices)
    {
        var accumX = new double[positions.Length];
        var accumY = new double[positions.Length];
        var accumZ = new double[positions.Length];

        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            AccumulateTriangleNormal(positions, indices[i], indices[i + 1], indices[i + 2], accumX, accumY, accumZ);
        }

        var encoded = new byte[positions.Length * 2];
        for (var i = 0; i < positions.Length; i++)
        {
            var normal = NormalizeOrRadial(accumX[i], accumY[i], accumZ[i], positions[i]);
            var (octX, octY) = OctEncode(normal.X, normal.Y, normal.Z);
            encoded[i * 2] = octX;
            encoded[(i * 2) + 1] = octY;
        }

        return encoded;
    }

    public static (byte X, byte Y) OctEncode(double nx, double ny, double nz)
    {
        var l1 = Math.Abs(nx) + Math.Abs(ny) + Math.Abs(nz);
        if (l1 <= 1e-12)
        {
            return (128, 128);
        }

        var px = nx / l1;
        var py = ny / l1;
        var ox = nz < 0.0 ? (1.0 - Math.Abs(py)) * SignNotZero(px) : px;
        var oy = nz < 0.0 ? (1.0 - Math.Abs(px)) * SignNotZero(py) : py;
        return (ToSignedNormByte(ox), ToSignedNormByte(oy));
    }

    public static (double X, double Y, double Z) OctDecode(byte x, byte y)
    {
        var ox = ((x / 255.0) * 2.0) - 1.0;
        var oy = ((y / 255.0) * 2.0) - 1.0;
        var oz = 1.0 - Math.Abs(ox) - Math.Abs(oy);

        var nx = oz < 0.0 ? (1.0 - Math.Abs(oy)) * SignNotZero(ox) : ox;
        var ny = oz < 0.0 ? (1.0 - Math.Abs(ox)) * SignNotZero(oy) : oy;
        var len = Math.Sqrt((nx * nx) + (ny * ny) + (oz * oz));
        return len <= 1e-12 ? (0.0, 0.0, 1.0) : (nx / len, ny / len, oz / len);
    }

    private static double ComputePointOcclusionMagnitude(EcefPoint scaled, EcefPoint unitDirection)
    {
        var magnitude = Length(scaled);
        if (magnitude <= 1e-12)
        {
            return double.PositiveInfinity;
        }

        var unitPosition = new EcefPoint(scaled.X / magnitude, scaled.Y / magnitude, scaled.Z / magnitude);
        var cosAlpha = Math.Clamp(Dot(unitPosition, unitDirection), -1.0, 1.0);
        var sinAlpha = Math.Sqrt(Math.Max(0.0, 1.0 - (cosAlpha * cosAlpha)));
        var cosBeta = 1.0 / Math.Max(magnitude, 1.0);
        var sinBeta = Math.Sqrt(Math.Max(0.0, 1.0 - (cosBeta * cosBeta)));

        var denom = (cosAlpha * cosBeta) - (sinAlpha * sinBeta);
        return denom <= 1e-6 ? double.PositiveInfinity : 1.0 / denom;
    }

    private static void AccumulateTriangleNormal(
        ReadOnlySpan<EcefPoint> positions,
        uint i0,
        uint i1,
        uint i2,
        double[] accumX,
        double[] accumY,
        double[] accumZ)
    {
        var p0 = positions[(int)i0];
        var p1 = positions[(int)i1];
        var p2 = positions[(int)i2];

        var e1x = p1.X - p0.X;
        var e1y = p1.Y - p0.Y;
        var e1z = p1.Z - p0.Z;
        var e2x = p2.X - p0.X;
        var e2y = p2.Y - p0.Y;
        var e2z = p2.Z - p0.Z;

        var nx = (e1y * e2z) - (e1z * e2y);
        var ny = (e1z * e2x) - (e1x * e2z);
        var nz = (e1x * e2y) - (e1y * e2x);

        accumX[i0] += nx;
        accumY[i0] += ny;
        accumZ[i0] += nz;
        accumX[i1] += nx;
        accumY[i1] += ny;
        accumZ[i1] += nz;
        accumX[i2] += nx;
        accumY[i2] += ny;
        accumZ[i2] += nz;
    }

    private static EcefPoint NormalizeOrRadial(double nx, double ny, double nz, EcefPoint fallbackPosition)
    {
        var len = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        if (len > 1e-12)
        {
            return new EcefPoint(nx / len, ny / len, nz / len);
        }

        var radialLen = Length(fallbackPosition);
        return radialLen > 1e-12
            ? new EcefPoint(fallbackPosition.X / radialLen, fallbackPosition.Y / radialLen, fallbackPosition.Z / radialLen)
            : new EcefPoint(0.0, 0.0, 1.0);
    }

    private static EcefPoint ScaleToUnitEllipsoid(EcefPoint point)
        => new(point.X / SemiMajorAxis, point.Y / SemiMajorAxis, point.Z / SemiMinorAxis);

    private static double Length(EcefPoint point)
        => Math.Sqrt((point.X * point.X) + (point.Y * point.Y) + (point.Z * point.Z));

    private static double Dot(EcefPoint left, EcefPoint right)
        => (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);

    private static double SignNotZero(double value) => value < 0.0 ? -1.0 : 1.0;

    private static byte ToSignedNormByte(double value)
        => (byte)Math.Clamp((int)Math.Round(((Math.Clamp(value, -1.0, 1.0) * 0.5) + 0.5) * 255.0, MidpointRounding.AwayFromZero), 0, 255);
}
