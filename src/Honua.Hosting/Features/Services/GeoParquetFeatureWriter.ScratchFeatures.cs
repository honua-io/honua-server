// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Apache.Arrow;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Infrastructure.Services;

public static partial class GeoParquetFeatureWriter
{
    private enum ScratchValue : byte
    {
        Null, String, Boolean, Byte, SByte, Int16, UInt16, Int32, UInt32, Int64, UInt64,
        Single, Double, Decimal, DateTime, DateTimeOffset, DateOnly, TimeOnly, TimeSpan,
        Guid, Binary, Json, Undefined, Char, DbNull, Opaque,
    }

    private static void WriteScratchFeature(BinaryWriter writer, Feature feature, StreamingEncoding encoding)
    {
        writer.Write(feature.Id);
        writer.Write(feature.Geometry?.Length ?? -1);
        if (feature.Geometry is not null)
        {
            writer.Write(feature.Geometry);
        }
        writer.Write(feature.Attributes.KeyComparer == StringComparer.OrdinalIgnoreCase);
        var attributes = feature.Attributes.Where(pair => ShouldSpillAttribute(pair.Key, encoding)).ToArray();
        writer.Write(attributes.Length);
        foreach (var (key, _) in attributes)
        {
            writer.Write(key);
            WriteScratchValue(writer, NormalizeScratchValue(feature, key, encoding));
        }
    }

    private static bool ShouldSpillAttribute(string key, StreamingEncoding encoding)
    {
        var fields = encoding.Options.OutFields;
        return !key.StartsWith("__", StringComparison.Ordinal)
            && (fields is null || fields.Length == 0 || (fields.Length == 1 && fields[0] == "*")
                || fields.Contains(key, StringComparer.OrdinalIgnoreCase));
    }

    private static object? NormalizeScratchValue(Feature feature, string key, StreamingEncoding encoding)
    {
        var value = feature.Attributes[key];
        if (value is null or string or bool or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal or DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan
            or Guid or byte[] or JsonElement or char or DBNull)
        {
            return value;
        }
        var field = encoding.Resource.SchemaFields.FirstOrDefault(field => field.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            if (value is Enum enumeration)
            {
                return Convert.ChangeType(enumeration, Enum.GetUnderlyingType(enumeration.GetType()), CultureInfo.InvariantCulture);
            }
            // Unknown runtime types do not participate in schema inference, but their
            // string representation can still fill a string column discovered elsewhere.
            return new ScratchOpaque(value.ToString() ?? string.Empty);
        }
        using var array = BuildAttributeArray([feature], key, field, encoding.Options.Logger);
        return array switch
        {
            Int64Array a => a.GetValue(0), Int32Array a => a.GetValue(0), Int16Array a => a.GetValue(0),
            DoubleArray a => a.GetValue(0), FloatArray a => a.GetValue(0), BooleanArray a => a.GetValue(0),
            StringArray a => a.GetString(0), Date32Array a => a.GetDateOnly(0),
            TimestampArray a => a.GetTimestamp(0), Time64Array a => a.GetValue(0) is { } ticks ? TimeSpan.FromTicks(ticks * 10) : null,
            BinaryArray a => a.IsNull(0) ? null : a.GetBytes(0).ToArray(),
            _ => null,
        };
    }

    private sealed record ScratchOpaque(string Text)
    {
        public override string ToString() => Text;
    }

    private static Feature ReadScratchFeature(BinaryReader reader)
    {
        var id = reader.ReadInt64();
        var length = reader.ReadInt32();
        var geometry = length < 0 ? null : reader.ReadBytes(length);
        var comparer = reader.ReadBoolean() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var count = reader.ReadInt32();
        var attributes = ImmutableDictionary.CreateBuilder<string, object?>(comparer);
        for (var index = 0; index < count; index++)
        {
            attributes.Add(reader.ReadString(), ReadScratchValue(reader));
        }
        return Feature.Create(id, geometry, attributes.ToImmutable());
    }

    private static void WriteScratchValue(BinaryWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.Write((byte)ScratchValue.Null); break;
            case string v: writer.Write((byte)ScratchValue.String); writer.Write(v); break;
            case char v: writer.Write((byte)ScratchValue.Char); writer.Write((ushort)v); break;
            case DBNull: writer.Write((byte)ScratchValue.DbNull); break;
            case ScratchOpaque v: writer.Write((byte)ScratchValue.Opaque); writer.Write(v.Text); break;
            case bool v: writer.Write((byte)ScratchValue.Boolean); writer.Write(v); break;
            case byte v: writer.Write((byte)ScratchValue.Byte); writer.Write(v); break;
            case sbyte v: writer.Write((byte)ScratchValue.SByte); writer.Write(v); break;
            case short v: writer.Write((byte)ScratchValue.Int16); writer.Write(v); break;
            case ushort v: writer.Write((byte)ScratchValue.UInt16); writer.Write(v); break;
            case int v: writer.Write((byte)ScratchValue.Int32); writer.Write(v); break;
            case uint v: writer.Write((byte)ScratchValue.UInt32); writer.Write(v); break;
            case long v: writer.Write((byte)ScratchValue.Int64); writer.Write(v); break;
            case ulong v: writer.Write((byte)ScratchValue.UInt64); writer.Write(v); break;
            case float v: writer.Write((byte)ScratchValue.Single); writer.Write(v); break;
            case double v: writer.Write((byte)ScratchValue.Double); writer.Write(v); break;
            case decimal v: writer.Write((byte)ScratchValue.Decimal); writer.Write(v); break;
            case DateTime v: writer.Write((byte)ScratchValue.DateTime); writer.Write(v.ToBinary()); break;
            case DateTimeOffset v:
                writer.Write((byte)ScratchValue.DateTimeOffset); writer.Write(v.Ticks); writer.Write(v.Offset.Ticks); break;
            case DateOnly v: writer.Write((byte)ScratchValue.DateOnly); writer.Write(v.DayNumber); break;
            case TimeOnly v: writer.Write((byte)ScratchValue.TimeOnly); writer.Write(v.Ticks); break;
            case TimeSpan v: writer.Write((byte)ScratchValue.TimeSpan); writer.Write(v.Ticks); break;
            case Guid v: writer.Write((byte)ScratchValue.Guid); writer.Write(v.ToByteArray()); break;
            case byte[] v: writer.Write((byte)ScratchValue.Binary); writer.Write(v.Length); writer.Write(v); break;
            case JsonElement { ValueKind: JsonValueKind.Undefined }: writer.Write((byte)ScratchValue.Undefined); break;
            case JsonElement v: writer.Write((byte)ScratchValue.Json); writer.Write(v.GetRawText()); break;
            default: throw new NotSupportedException("Unsupported GeoParquet scratch attribute type.");
        }
    }

    private static object? ReadScratchValue(BinaryReader reader) => (ScratchValue)reader.ReadByte() switch
    {
        ScratchValue.Null => null,
        ScratchValue.String => reader.ReadString(),
        ScratchValue.Char => (char)reader.ReadUInt16(),
        ScratchValue.DbNull => DBNull.Value,
        ScratchValue.Opaque => new ScratchOpaque(reader.ReadString()),
        ScratchValue.Boolean => reader.ReadBoolean(),
        ScratchValue.Byte => reader.ReadByte(),
        ScratchValue.SByte => reader.ReadSByte(),
        ScratchValue.Int16 => reader.ReadInt16(),
        ScratchValue.UInt16 => reader.ReadUInt16(),
        ScratchValue.Int32 => reader.ReadInt32(),
        ScratchValue.UInt32 => reader.ReadUInt32(),
        ScratchValue.Int64 => reader.ReadInt64(),
        ScratchValue.UInt64 => reader.ReadUInt64(),
        ScratchValue.Single => reader.ReadSingle(),
        ScratchValue.Double => reader.ReadDouble(),
        ScratchValue.Decimal => reader.ReadDecimal(),
        ScratchValue.DateTime => DateTime.FromBinary(reader.ReadInt64()),
        ScratchValue.DateTimeOffset => new DateTimeOffset(reader.ReadInt64(), new TimeSpan(reader.ReadInt64())),
        ScratchValue.DateOnly => DateOnly.FromDayNumber(reader.ReadInt32()),
        ScratchValue.TimeOnly => new TimeOnly(reader.ReadInt64()),
        ScratchValue.TimeSpan => new TimeSpan(reader.ReadInt64()),
        ScratchValue.Guid => new Guid(reader.ReadBytes(16)),
        ScratchValue.Binary => reader.ReadBytes(reader.ReadInt32()),
        ScratchValue.Json => ReadScratchJson(reader.ReadString()),
        ScratchValue.Undefined => default(JsonElement),
        _ => throw new InvalidDataException("Invalid GeoParquet scratch value."),
    };

    private static JsonElement ReadScratchJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
