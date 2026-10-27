// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using FlatGeobuf;
using Google.FlatBuffers;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Implementation;
using NetTopologySuite.IO;
using NetTopologySuite.Shape.Fractal;
using Feature = Honua.Core.Features.FeatureStore.Domain.Feature;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using GeometryType = FlatGeobuf.GeometryType;

namespace Honua.Core.Features.FeatureExport;

/// <summary>
/// Encodes already authorized, projected feature pages without a database-native property encoder.
/// Header and feature tables use the format's generated FlatBuffers schema; the optional index
/// contains complete rows in Hilbert order. Missing and empty geometries retain their rows.
/// </summary>
internal static class FlatGeobufFeatureWriter
{
    private const ushort IndexNodeSize = 16;

    internal static byte[] Write(
        IReadOnlyList<Feature> features,
        MetadataV2Resource resource,
        FeatureQuery query,
        IEnumerable<MetadataV2Field> projectedFields,
        int? outputSrid,
        CancellationToken cancellationToken)
    {
        var fields = query.ExcludeAttributes ? [] : projectedFields
            .Where(field => !field.Name.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var columns = new List<ColumnT>
        {
            new() { Name = FieldNames.ObjectId, Type = ColumnType.Long, Nullable = false, PrimaryKey = !query.Distinct }
        };
        columns.AddRange(fields.Select(field => new ColumnT
        {
            Name = field.Name,
            Type = ColumnTypeFor(field.Type),
            Nullable = field.Nullable
        }));
        if (columns.Count > ushort.MaxValue || columns.Select(column => column.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Count)
        {
            throw new InvalidDataException("FlatGeobuf requires unique projected column names within its property-index limit.");
        }

        var reader = new WKBReader(new NtsGeometryServices(PackedCoordinateSequenceFactory.DoubleFactory));
        var rows = new List<Row>(features.Count);
        var envelope = new Envelope();
        var canIndex = features.Count > 0;
        var hasZ = false;
        var hasM = false;
        foreach (var feature in features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = feature.Geometry is { Length: > 0 } ? reader.Read(feature.Geometry) : null;
            if (geometry is not null)
            {
                hasZ |= query.IncludeZ && HasDimension(geometry, z: true);
                hasM |= query.IncludeM && HasDimension(geometry, z: false);
            }
            var bounds = geometry?.EnvelopeInternal;
            if (geometry is null || geometry.IsEmpty || bounds is null || bounds.IsNull ||
                !double.IsFinite(bounds.MinX) || !double.IsFinite(bounds.MinY) ||
                !double.IsFinite(bounds.MaxX) || !double.IsFinite(bounds.MaxY))
            {
                canIndex = false;
            }
            else
            {
                envelope.ExpandToInclude(bounds);
            }
            rows.Add(new Row(feature, bounds));
        }

        if (canIndex)
        {
            rows = rows.OrderByDescending(row => Hilbert(row.Bounds!, envelope)).ToList();
        }

        var srid = outputSrid;
        var header = new HeaderT
        {
            Name = resource.Metadata.Name,
            GeometryType = GeometryType.Unknown,
            HasZ = hasZ,
            HasM = hasM,
            Columns = columns,
            FeaturesCount = (ulong)rows.Count,
            IndexNodeSize = canIndex ? IndexNodeSize : (ushort)0,
            Crs = srid is > 0 ? new CrsT { Org = "EPSG", Code = srid.Value } : null,
            Envelope = envelope.IsNull ? null : [envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY]
        };
        var headerBuilder = new FlatBufferBuilder(1024);
        headerBuilder.FinishSizePrefixed(Header.Pack(headerBuilder, header).Value);

        using var output = new MemoryStream();
        output.Write(Constants.MagicBytes);
        output.Write(headerBuilder.SizedByteArray());
        var indexStart = output.Position;
        if (canIndex)
        {
            output.SetLength(checked(indexStart + (long)FlatGeobuf.Index.PackedRTree.CalcSize((ulong)rows.Count, IndexNodeSize)));
            output.Position = output.Length;
        }
        var featureStart = output.Position;
        var indexLeaves = new List<IndexNode>(rows.Count);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var builder = new FlatBufferBuilder(1024);
            var geometry = row.Feature.Geometry is { Length: > 0 } ? reader.Read(row.Feature.Geometry) : null;
            var encoded = new FeatureT
            {
                Geometry = geometry is null ? null : EncodeGeometry(geometry, hasZ, hasM),
                Properties = EncodeProperties(row.Feature, fields).ToList()
            };
            builder.FinishSizePrefixed(FlatGeobuf.Feature.Pack(builder, encoded).Value);
            if (canIndex)
            {
                indexLeaves.Add(new IndexNode(row.Bounds!, (ulong)(output.Position - featureStart)));
            }
            output.Write(builder.SizedByteArray());
        }

        if (canIndex)
        {
            output.Position = indexStart;
            WriteIndex(output, indexLeaves);
        }
        return output.ToArray();
    }

    private static ColumnType ColumnTypeFor(MetadataV2FieldType type) => type switch
    {
        MetadataV2FieldType.Integer => ColumnType.Int,
        MetadataV2FieldType.BigInteger => ColumnType.Long,
        MetadataV2FieldType.Float => ColumnType.Float,
        MetadataV2FieldType.Double => ColumnType.Double,
        MetadataV2FieldType.Boolean => ColumnType.Bool,
        MetadataV2FieldType.DateTime => ColumnType.DateTime,
        MetadataV2FieldType.Json => ColumnType.Json,
        MetadataV2FieldType.Binary => ColumnType.Binary,
        // The format has no standalone date, wall-clock time, or UUID type.
        // Preserve these values as ISO/date/time and canonical UUID strings.
        _ => ColumnType.String
    };

    private static byte[] EncodeProperties(Feature feature, MetadataV2Field[] fields)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0);
        writer.Write(feature.Id);
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            var value = FindValue(feature, field.Name);
            if (value is null || value is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
            {
                continue;
            }
            writer.Write(checked((ushort)(index + 1)));
            switch (ColumnTypeFor(field.Type))
            {
                case ColumnType.Int:
                    writer.Write(checked((int)ReadInteger(value)));
                    break;
                case ColumnType.Long:
                    writer.Write(ReadInteger(value));
                    break;
                case ColumnType.Float:
                    writer.Write(Convert.ToSingle(UnwrapScalar(value), CultureInfo.InvariantCulture));
                    break;
                case ColumnType.Double:
                    writer.Write(Convert.ToDouble(UnwrapScalar(value), CultureInfo.InvariantCulture));
                    break;
                case ColumnType.Bool:
                    writer.Write(Convert.ToBoolean(UnwrapScalar(value), CultureInfo.InvariantCulture));
                    break;
                case ColumnType.Binary:
                    WriteBytes(writer, BinaryValue(value));
                    break;
                case ColumnType.Json:
                    var json = value is JsonElement element ? element.GetRawText() : ScalarText(value);
                    using (JsonDocument.Parse(json))
                    {
                        WriteBytes(writer, Encoding.UTF8.GetBytes(json));
                    }
                    break;
                default:
                    WriteBytes(writer, Encoding.UTF8.GetBytes(FormatText(value, field.Type)));
                    break;
            }
        }
        return stream.ToArray();
    }

    private static object? FindValue(Feature feature, string name)
    {
        if (feature.Attributes.TryGetValue(name, out var value))
        {
            return value;
        }
        foreach (var pair in feature.Attributes)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }
        return null;
    }

    private static object UnwrapScalar(object value) => value is JsonElement element
        ? element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.GetRawText(),
            _ => throw new InvalidDataException("A scalar FlatGeobuf property contains a structured value.")
        }
        : value;

    private static long ReadInteger(object value)
    {
        var number = Convert.ToDecimal(UnwrapScalar(value), CultureInfo.InvariantCulture);
        if (decimal.Truncate(number) != number)
        {
            throw new InvalidDataException("An integer FlatGeobuf property contains a fractional value.");
        }
        return checked((long)number);
    }

    private static string ScalarText(object value) => Convert.ToString(UnwrapScalar(value), CultureInfo.InvariantCulture) ?? string.Empty;

    private static string FormatText(object value, MetadataV2FieldType type)
    {
        value = UnwrapScalar(value);
        if (type == MetadataV2FieldType.Uuid)
        {
            return (value is Guid guid ? guid : Guid.Parse(ScalarText(value))).ToString("D");
        }
        if (type == MetadataV2FieldType.DateTime)
        {
            return value switch
            {
                DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
                DateTime date => (date.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : date.ToUniversalTime()).ToString("O", CultureInfo.InvariantCulture),
                long milliseconds => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToString("O", CultureInfo.InvariantCulture),
                double milliseconds => DateTimeOffset.UnixEpoch.AddMilliseconds(milliseconds).ToString("O", CultureInfo.InvariantCulture),
                _ => DateTimeOffset.Parse(ScalarText(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
                    .ToString("O", CultureInfo.InvariantCulture)
            };
        }
        return value switch
        {
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan time => time.ToString("c", CultureInfo.InvariantCulture),
            _ => ScalarText(value)
        };
    }

    private static void WriteBytes(BinaryWriter writer, byte[] bytes)
    {
        writer.Write(checked((uint)bytes.Length));
        writer.Write(bytes);
    }

    private static byte[] BinaryValue(object value)
    {
        if (value is byte[] bytes)
        {
            return bytes;
        }
        // PostgreSQL's JSON representation of bytea is hex; JSON API binary values
        // use base64. Preserve the octets from either canonical materialization.
        var text = ScalarText(value);
        return text.StartsWith("\\x", StringComparison.Ordinal)
            ? Convert.FromHexString(text.AsSpan(2))
            : Convert.FromBase64String(text);
    }

    private static bool HasDimension(NtsGeometry geometry, bool z) => geometry switch
    {
        Point point => z ? point.CoordinateSequence.HasZ : point.CoordinateSequence.HasM,
        LineString line => z ? line.CoordinateSequence.HasZ : line.CoordinateSequence.HasM,
        Polygon polygon => HasDimension(polygon.ExteriorRing, z) || polygon.InteriorRings.Any(ring => HasDimension(ring, z)),
        GeometryCollection collection => collection.Geometries.Any(part => HasDimension(part, z)),
        _ => false
    };

    private static GeometryT EncodeGeometry(NtsGeometry geometry, bool hasZ, bool hasM)
    {
        var result = new GeometryT { Type = GeometryConversionsType(geometry) };
        if (geometry is MultiPolygon || geometry is GeometryCollection and not (MultiPoint or MultiLineString))
        {
            result.Parts = ((GeometryCollection)geometry).Geometries
                .Select(part => EncodeGeometry(part, hasZ, hasM)).ToList();
            return result;
        }
        // Geometry.Coordinates omits empty members of a MultiPoint. Keep their
        // positions as NaN points so a valid component is never silently removed.
        var coordinates = geometry is MultiPoint points
            ? points.Geometries.Select(point => point.IsEmpty
                ? new CoordinateZM(double.NaN, double.NaN, double.NaN, double.NaN)
                : point.Coordinate).ToArray()
            : geometry.Coordinates;
        if (geometry is Point && geometry.IsEmpty)
        {
            // OGC empty points use NaN ordinates; both GDAL and NTS preserve EMPTY.
            result.Xy = [double.NaN, double.NaN];
            result.Z = hasZ ? [double.NaN] : null;
            result.M = hasM ? [double.NaN] : null;
            return result;
        }
        result.Xy = coordinates.SelectMany(coordinate => new[] { coordinate.X, coordinate.Y }).ToList();
        result.Z = hasZ ? coordinates.Select(coordinate => coordinate.Z).ToList() : null;
        result.M = hasM ? coordinates.Select(coordinate => coordinate.M).ToList() : null;
        IEnumerable<NtsGeometry>? parts = geometry switch
        {
            Polygon polygon => new NtsGeometry[] { polygon.ExteriorRing }.Concat(polygon.InteriorRings),
            MultiLineString lines => lines.Geometries,
            _ => null
        };
        if (parts is not null)
        {
            uint end = 0;
            result.Ends = parts.Select(part => end = checked(end + (uint)part.NumPoints)).ToList();
        }
        return result;
    }

    private static GeometryType GeometryConversionsType(NtsGeometry geometry) => geometry switch
    {
        Point => GeometryType.Point,
        MultiPoint => GeometryType.MultiPoint,
        LineString => GeometryType.LineString,
        MultiLineString => GeometryType.MultiLineString,
        Polygon => GeometryType.Polygon,
        MultiPolygon => GeometryType.MultiPolygon,
        GeometryCollection => GeometryType.GeometryCollection,
        _ => throw new InvalidDataException("The geometry type cannot be represented by the FlatGeobuf writer.")
    };

    private static uint Hilbert(Envelope bounds, Envelope extent)
    {
        static int Ordinate(double minimum, double maximum, double center)
        {
            var width = maximum / 2 - minimum / 2;
            return width == 0 ? 0 : (int)Math.Clamp((center / 2 - minimum / 2) / width * 65535, 0, 65535);
        }
        var x = Ordinate(extent.MinX, extent.MaxX, bounds.MinX / 2 + bounds.MaxX / 2);
        var y = Ordinate(extent.MinY, extent.MaxY, bounds.MinY / 2 + bounds.MaxY / 2);
        return unchecked((uint)HilbertCode.Encode(16, x, y));
    }

    private static void WriteIndex(Stream stream, List<IndexNode> leaves)
    {
        var counts = new List<int> { leaves.Count };
        do
        {
            counts.Add((counts[^1] + IndexNodeSize - 1) / IndexNodeSize);
        } while (counts[^1] != 1);
        var total = counts.Sum();
        var offsets = new int[counts.Count];
        var remaining = total;
        for (var level = 0; level < counts.Count; level++)
        {
            remaining -= counts[level];
            offsets[level] = remaining;
        }
        var nodes = new IndexNode[total];
        leaves.CopyTo(nodes, offsets[0]);
        for (var level = 1; level < counts.Count; level++)
        {
            for (var index = 0; index < counts[level]; index++)
            {
                var first = index * IndexNodeSize;
                var bounds = new Envelope();
                for (var child = first; child < Math.Min(first + IndexNodeSize, counts[level - 1]); child++)
                {
                    bounds.ExpandToInclude(nodes[offsets[level - 1] + child].Bounds);
                }
                nodes[offsets[level] + index] = new IndexNode(bounds, (ulong)(offsets[level - 1] + first));
            }
        }
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        foreach (var node in nodes)
        {
            writer.Write(node.Bounds.MinX);
            writer.Write(node.Bounds.MinY);
            writer.Write(node.Bounds.MaxX);
            writer.Write(node.Bounds.MaxY);
            writer.Write(node.Offset);
        }
    }

    private sealed record Row(Feature Feature, Envelope? Bounds);
    private readonly record struct IndexNode(Envelope Bounds, ulong Offset);
}
