// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

// Projects WKB or EWKB straight into a gRPC geometry, without a NetTopologySuite geometry in between.
// Only the common simple types are handled. Everything else (empty or invalid parts, collections,
// curves, trailing or missing bytes) yields null, and the caller takes the NetTopologySuite path,
// so results and errors stay identical to it.
internal static class GrpcWkbProjection
{
    private const uint EwkbZ = 0x80000000;
    private const uint EwkbM = 0x40000000;
    private const uint EwkbSrid = 0x20000000;

    // A negative precision leaves coordinates unrounded, like the absent precision limit.
    public static Proto.Geometry? Project(ReadOnlySpan<byte> wkb, int precision)
    {
        var reader = new Reader(wkb, precision);
        try
        {
            var geometry = reader.ReadGeometry();
            return reader.AtEnd ? geometry : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            // Truncated input: NetTopologySuite reports it.
            return null;
        }
    }

    private enum Shape { Point = 1, LineString = 2, Polygon = 3, MultiPoint = 4, MultiLineString = 5, MultiPolygon = 6 }

    private readonly record struct ReadSequence(Proto.CoordinateSequence Sequence, (double X, double Y) First, (double X, double Y) Last);

    private ref struct Reader(ReadOnlySpan<byte> data, int precision)
    {
        private ReadOnlySpan<byte> _rest = data;
        private bool _little;
        private bool _hasZ;
        private bool _hasM;

        public readonly bool AtEnd => _rest.IsEmpty;

        public Proto.Geometry? ReadGeometry() => ReadHeader() switch
        {
            Shape.Point => ReadPoint() is { } point ? new Proto.Geometry { Point = point } : null,
            Shape.LineString => ReadPath() is { } path ? new Proto.Geometry { Polyline = new() { Paths = { path } } } : null,
            Shape.Polygon => ReadPolygon() is { } polygon ? new Proto.Geometry { Polygon = polygon } : null,
            Shape.MultiPoint => ReadMultiPoint() is { } points ? new Proto.Geometry { MultiPoint = points } : null,
            Shape.MultiLineString => ReadMultiLineString() is { } lines ? new Proto.Geometry { Polyline = lines } : null,
            Shape.MultiPolygon => ReadMultiPolygon() is { } polygons ? new Proto.Geometry { MultiPolygon = polygons } : null,
            _ => null,
        };

        private Shape? ReadHeader()
        {
            var order = ReadByte();
            if (order > 1)
            {
                return null;
            }

            _little = order == 1;
            var type = ReadUInt32();
            if ((type & EwkbSrid) != 0)
            {
                _ = ReadUInt32();
            }

            return DecodeType(type);
        }

        private Shape? DecodeType(uint type)
        {
            // As NetTopologySuite reads it: an ISO thousands digit names the dimensions, and the EWKB
            // flags count only without one (writers may set both).
            var code = type & 0xFFFF;
            var iso = code / 1000;
            _hasZ = iso == 0 ? (type & EwkbZ) != 0 : iso is 1 or 3;
            _hasM = iso == 0 ? (type & EwkbM) != 0 : iso is 2 or 3;
            var baseCode = code % 1000;
            return iso <= 3 && baseCode is >= 1 and <= 6 ? (Shape)baseCode : null;
        }

        private Proto.PointGeometry? ReadPoint()
        {
            var x = ReadDouble();
            var y = ReadDouble();
            var z = _hasZ ? ReadDouble() : double.NaN;
            var m = _hasM ? ReadDouble() : double.NaN;
            if (double.IsNaN(x) || double.IsNaN(y))
            {
                // An empty point is NaN, NaN; NetTopologySuite reports it as empty.
                return null;
            }

            var point = new Proto.PointGeometry { X = Round(x), Y = Round(y) };
            if (!double.IsNaN(z))
            {
                point.Z = Round(z);
            }

            if (!double.IsNaN(m))
            {
                point.M = Round(m);
            }

            return point;
        }

        private Proto.CoordinateSequence? ReadPath()
            => ReadCoordinates(minimum: 2) is { } read ? read.Sequence : null;

        // Closure is judged on the stored coordinates, before rounding, as NetTopologySuite does.
        private Proto.CoordinateSequence? ReadRing()
            => ReadCoordinates(minimum: 4) is { } read && Same(read.First.X, read.Last.X) && Same(read.First.Y, read.Last.Y)
                ? read.Sequence
                : null;

        // Exact coordinate identity, as NetTopologySuite's ring check: NaN never matches.
        private static bool Same(double a, double b) => !double.IsNaN(a) && a.CompareTo(b) == 0;

        private ReadSequence? ReadCoordinates(int minimum)
        {
            var count = ReadCount();
            if (count < minimum)
            {
                return null;
            }

            var sequence = new Proto.CoordinateSequence { Coords = { Capacity = count } };
            (double X, double Y) first = default, last = default;
            for (var i = 0; i < count; i++)
            {
                (var coordinate, last) = ReadCoordinate();
                sequence.Coords.Add(coordinate);
                first = i == 0 ? last : first;
            }

            return new ReadSequence(sequence, first, last);
        }

        private (Proto.Coordinate Coordinate, (double X, double Y) Raw) ReadCoordinate()
        {
            var raw = (X: ReadDouble(), Y: ReadDouble());
            var coordinate = new Proto.Coordinate { X = Round(raw.X), Y = Round(raw.Y) };
            if (_hasZ && ReadDouble() is var z && !double.IsNaN(z))
            {
                coordinate.Z = Round(z);
            }

            if (_hasM && ReadDouble() is var m && !double.IsNaN(m))
            {
                coordinate.M = Round(m);
            }

            return (coordinate, raw);
        }

        private Proto.PolygonGeometry? ReadPolygon()
        {
            var polygon = new Proto.PolygonGeometry();
            var rings = ReadCount();
            for (var i = 0; i < rings; i++)
            {
                if (ReadRing() is not { } ring)
                {
                    return null;
                }

                polygon.Rings.Add(ring);
            }

            return rings > 0 ? polygon : null;
        }

        private Proto.MultiPointGeometry? ReadMultiPoint()
        {
            var multi = new Proto.MultiPointGeometry();
            var count = ReadCount();
            for (var i = 0; i < count; i++)
            {
                if (!ReadPart(Shape.Point) || ReadPoint() is not { } point)
                {
                    return null;
                }

                multi.Points.Add(point);
            }

            return count > 0 ? multi : null;
        }

        private Proto.PolylineGeometry? ReadMultiLineString()
        {
            var polyline = new Proto.PolylineGeometry();
            var count = ReadCount();
            for (var i = 0; i < count; i++)
            {
                if (!ReadPart(Shape.LineString) || ReadPath() is not { } path)
                {
                    return null;
                }

                polyline.Paths.Add(path);
            }

            return count > 0 ? polyline : null;
        }

        private Proto.MultiPolygonGeometry? ReadMultiPolygon()
        {
            var multi = new Proto.MultiPolygonGeometry();
            var count = ReadCount();
            for (var i = 0; i < count; i++)
            {
                if (!ReadPart(Shape.Polygon) || ReadPolygon() is not { } polygon)
                {
                    return null;
                }

                multi.Polygons.Add(polygon);
            }

            return count > 0 ? multi : null;
        }

        // A part keeps the parent's dimensions; a part that disagrees is left to NetTopologySuite.
        private bool ReadPart(Shape expected)
        {
            var (hasZ, hasM) = (_hasZ, _hasM);
            return ReadHeader() == expected && _hasZ == hasZ && _hasM == hasM;
        }

        private readonly double Round(double value)
            => precision < 0 ? value : Math.Round(value, Math.Clamp(precision, 0, 15), MidpointRounding.AwayFromZero);

        // Each element needs at least one coordinate pair, so a larger count is invalid (-1).
        private int ReadCount()
        {
            var count = ReadUInt32();
            return count > (uint)_rest.Length / (2 * sizeof(double)) + 1 ? -1 : (int)count;
        }

        private byte ReadByte()
        {
            if (_rest.IsEmpty)
            {
                return byte.MaxValue;
            }

            var value = _rest[0];
            _rest = _rest[1..];
            return value;
        }

        private uint ReadUInt32()
        {
            var value = _little ? BinaryPrimitives.ReadUInt32LittleEndian(_rest) : BinaryPrimitives.ReadUInt32BigEndian(_rest);
            _rest = _rest[sizeof(uint)..];
            return value;
        }

        private double ReadDouble()
        {
            var value = _little ? BinaryPrimitives.ReadDoubleLittleEndian(_rest) : BinaryPrimitives.ReadDoubleBigEndian(_rest);
            _rest = _rest[sizeof(double)..];
            return value;
        }
    }
}
