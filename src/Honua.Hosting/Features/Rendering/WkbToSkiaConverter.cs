// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using SkiaSharp;

namespace Honua.Infrastructure.Rendering;

internal static class WkbToSkiaConverter
{
    private const int WkbPoint = 1;

    internal readonly struct GeometryConversionResult
    {
        public SKPath? Path { get; init; }

        public SKPoint[]? Points { get; init; }

        public bool IsPoint { get; init; }

        public bool IsPolygon { get; init; }
    }

    public static GeometryConversionResult Convert(byte[] wkb, Func<double, double, SKPoint> transform)
    {
        if (wkb == null || wkb.Length < 5)
        {
            return default;
        }

        try
        {
            var decoder = new WkbToSkiaDecoder(wkb, transform);
            return decoder.ReadGeometry();
        }
        catch (InvalidDataException)
        {
            return default;
        }
    }

    public static bool TryConvertPoint(byte[]? wkb, Func<double, double, SKPoint> transform, out SKPoint point)
    {
        point = default;

        if (wkb == null || wkb.Length < 21)
        {
            return false;
        }

        var reader = new WkbReader(wkb);
        var byteOrder = reader.ReadByte();
        reader.IsLittleEndian = byteOrder == 1;

        var rawType = reader.ReadInt32();

        var hasSrid = (rawType & 0x20000000) != 0;
        var hasZ = (rawType & unchecked((int)0x80000000)) != 0;
        var hasM = (rawType & 0x40000000) != 0;

        var geometryType = rawType & 0xFFFF;

        if (geometryType >= 3000 && geometryType < 4000)
        {
            hasZ = true;
            hasM = true;
            geometryType -= 3000;
        }
        else if (geometryType >= 2000 && geometryType < 3000)
        {
            hasM = true;
            geometryType -= 2000;
        }
        else if (geometryType >= 1000 && geometryType < 2000)
        {
            hasZ = true;
            geometryType -= 1000;
        }

        if (geometryType != WkbPoint)
        {
            return false;
        }

        if (hasSrid)
        {
            if (wkb.Length < 25)
            {
                return false;
            }

            reader.ReadInt32();
        }

        var expectedLength = 1 + 4 + (hasSrid ? 4 : 0) + 16 + ((hasZ ? 1 : 0) + (hasM ? 1 : 0)) * 8;
        if (wkb.Length < expectedLength)
        {
            return false;
        }

        var (x, y) = reader.ReadCoordinate();
        point = transform(x, y);
        return true;
    }
}
