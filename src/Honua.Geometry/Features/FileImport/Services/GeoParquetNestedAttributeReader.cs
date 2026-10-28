// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Buffers;
using Apache.Arrow;
using Apache.Arrow.Types;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.FileImport.Services;

/// <summary>Reconstructs nested Parquet values through Arrow's canonical nested arrays.</summary>
internal static class GeoParquetNestedAttributeReader
{
    internal static async Task<Dictionary<string, List<JsonElement?>>> ReadRowGroupAsync(
        Stream stream, string[] names, int rowGroup, ImportLimits limits, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<JsonElement?>>(StringComparer.Ordinal);
        if (names.Length == 0)
        {
            return result;
        }

        using var reader = OpenReader(stream);
        var requested = names.ToHashSet(StringComparer.Ordinal);
        var columns = reader.SchemaManifest.SchemaFields
            .Where(field => requested.Contains(field.Field.Name)).SelectMany(GetLeafIndices).ToArray();
        using var batches = reader.GetRecordBatchReader([rowGroup], columns);
        long jsonBytes = 0;
        foreach (var name in names)
        {
            result.Add(name, []);
        }
        while (await batches.ReadNextRecordBatchAsync(cancellationToken) is { } batch)
        {
            using (batch)
            {
                var arrowBytes = batch.Arrays.Sum(array => GetBufferBytes(array.Data));
                if (limits.MaxMemoryBytes > 0 && arrowBytes + jsonBytes > limits.MaxMemoryBytes)
                {
                    throw new InvalidDataException("GeoParquet Arrow batch and retained nested attributes exceed ImportLimits.MaxMemoryBytes.");
                }
                foreach (var name in names)
                {
                    var column = batch.Column(name);
                    for (var row = 0; row < batch.Length; row++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (column.IsNull(row))
                        {
                            result[name].Add(null);
                            continue;
                        }
                        var remaining = limits.MaxMemoryBytes > 0
                            ? Math.Max(0, (limits.MaxMemoryBytes - arrowBytes - jsonBytes) / 4)
                            : int.MaxValue;
                        var bytes = new BoundedBufferWriter(checked((int)Math.Min(int.MaxValue, remaining)));
                        using (var writer = new Utf8JsonWriter(bytes))
                        {
                            WriteValue(writer, column, row, 0, cancellationToken);
                        }
                        jsonBytes = checked(jsonBytes + bytes.WrittenCount * 4L);
                        if (limits.MaxMemoryBytes > 0 && jsonBytes > limits.MaxMemoryBytes)
                        {
                            throw new InvalidDataException("GeoParquet nested attributes exceed ImportLimits.MaxMemoryBytes for a row group.");
                        }
                        using var document = JsonDocument.Parse(bytes.WrittenMemory);
                        result[name].Add(document.RootElement.Clone());
                    }
                }
            }
        }
        return result;
    }

    private static long UnitsPerSecond(TimeUnit unit) => unit switch
    {
        TimeUnit.Second => 1,
        TimeUnit.Millisecond => 1000,
        TimeUnit.Microsecond => 1000000,
        TimeUnit.Nanosecond => 1000000000,
        _ => throw new NotSupportedException("GeoParquet temporal unit is not supported.")
    };

    private static void WriteTimestamp(Utf8JsonWriter writer, long value, TimestampType type)
    {
        var scale = UnitsPerSecond(type.Unit);
        var seconds = Math.DivRem(value, scale, out var fraction);
        if (fraction < 0)
        {
            seconds--;
            fraction += scale;
        }
        var instant = DateTimeOffset.FromUnixTimeSeconds(seconds);
        var nanoseconds = fraction * (1000000000 / scale);
        var prefix = instant.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        writer.WriteStringValue(prefix + "." + nanoseconds.ToString("D9", System.Globalization.CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(type.Timezone) ? string.Empty : "Z"));
    }

    private static void WriteTime(Utf8JsonWriter writer, long value, TimeUnit unit)
    {
        var scale = UnitsPerSecond(unit);
        if (value < 0 || value >= 86400L * scale)
        {
            throw new InvalidDataException("GeoParquet time value is outside one day.");
        }
        var seconds = Math.DivRem(value, scale, out var fraction);
        var prefix = TimeOnly.FromTimeSpan(TimeSpan.FromSeconds(seconds))
            .ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        writer.WriteStringValue(prefix + "." + (fraction * (1000000000 / scale)).ToString("D9", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static IEnumerable<int> GetLeafIndices(ParquetSharp.Arrow.SchemaField field) =>
        field.ColumnIndex >= 0 ? [field.ColumnIndex] : field.Children.SelectMany(GetLeafIndices);

    private static long GetBufferBytes(ArrayData data) =>
        data.Buffers.Sum(buffer => (long)buffer.Length) + data.Children.Sum(GetBufferBytes);

    private sealed class BoundedBufferWriter(int maximumBytes) : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[Math.Min(256, maximumBytes)];
        public int WrittenCount { get; private set; }
        public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, WrittenCount);
        public void Advance(int count)
        {
            if (count < 0 || count > _buffer.Length - WrittenCount)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            WrittenCount += count;
        }
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Validate(sizeHint);
            var required = checked(WrittenCount + Math.Max(1, sizeHint));
            if (required > _buffer.Length)
            {
                var capacity = (int)Math.Min(maximumBytes, Math.Max((long)required, _buffer.Length * 2L));
                System.Array.Resize(ref _buffer, capacity);
            }
            return _buffer.AsMemory(WrittenCount);
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        private void Validate(int sizeHint)
        {
            if (Math.Max(1, sizeHint) > maximumBytes - WrittenCount)
            {
                throw new InvalidDataException("GeoParquet nested JSON allocation exceeds ImportLimits.MaxMemoryBytes.");
            }
        }
    }

    private static ParquetSharp.Arrow.FileReader OpenReader(Stream stream)
    {
        try
        {
            using var properties = ParquetSharp.Arrow.ArrowReaderProperties.GetDefault();
            properties.BatchSize = 256;
            properties.PreBuffer = false;
            properties.UseThreads = false;
            return new ParquetSharp.Arrow.FileReader(stream, arrowProperties: properties, leaveOpen: true);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException ||
                                   ex is TypeInitializationException { InnerException: DllNotFoundException or EntryPointNotFoundException or BadImageFormatException })
        {
            throw new NotSupportedException("GeoParquet nested attribute decoding is unavailable on this runtime image; a supported ParquetSharp native runtime is required.", ex);
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, IArrowArray array, int row, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > 32)
        {
            throw new InvalidDataException("GeoParquet nested attributes exceed the supported depth of 32.");
        }
        if (array.IsNull(row))
        {
            writer.WriteNullValue();
            return;
        }
        switch (array)
        {
            case MapArray map:
                writer.WriteStartObject();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                for (var index = map.GetValueOffset(row); index < map.GetValueOffset(row) + map.GetValueLength(row); index++)
                {
                    if (map.Keys is not StringArray strings || strings.IsNull(index))
                    {
                        throw new NotSupportedException("GeoParquet maps require non-null string keys for lossless JSON attributes.");
                    }
                    var key = strings.GetString(index);
                    if (!keys.Add(key))
                    {
                        throw new InvalidDataException("GeoParquet map contains duplicate keys that cannot be preserved in JSON attributes.");
                    }
                    writer.WritePropertyName(key);
                    WriteValue(writer, map.Values, index, depth + 1, cancellationToken);
                }
                writer.WriteEndObject();
                break;
            case ListArray list:
                writer.WriteStartArray();
                for (var index = list.GetValueOffset(row); index < list.GetValueOffset(row) + list.GetValueLength(row); index++)
                {
                    WriteValue(writer, list.Values, index, depth + 1, cancellationToken);
                }
                writer.WriteEndArray();
                break;
            case StructArray structure:
                writer.WriteStartObject();
                var fields = ((StructType)structure.Data.DataType).Fields;
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (var index = 0; index < fields.Count; index++)
                {
                    if (!names.Add(fields[index].Name))
                    {
                        throw new InvalidDataException("GeoParquet struct contains duplicate field names.");
                    }
                    writer.WritePropertyName(fields[index].Name);
                    WriteValue(writer, structure.Fields[index], row, depth + 1, cancellationToken);
                }
                writer.WriteEndObject();
                break;
            case Decimal32Array value: writer.WriteRawValue(value.GetString(row), skipInputValidation: false); break;
            case Decimal64Array value: writer.WriteRawValue(value.GetString(row), skipInputValidation: false); break;
            case Decimal256Array value: writer.WriteRawValue(value.GetString(row), skipInputValidation: false); break;
            case Decimal128Array value: writer.WriteRawValue(value.GetString(row), skipInputValidation: false); break;
            case TimestampArray value:
                WriteTimestamp(writer, value.GetValue(row)!.Value, (TimestampType)value.Data.DataType);
                break;
            case Date32Array value: writer.WriteStringValue(value.GetDateOnly(row)!.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)); break;
            case Date64Array value: writer.WriteStringValue(value.GetDateTimeOffset(row)!.Value); break;
            case Time32Array value: writer.WriteStringValue(value.GetTime(row)!.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture)); break;
            case Time64Array value:
                WriteTime(writer, value.GetValue(row)!.Value, ((Time64Type)value.Data.DataType).Unit);
                break;
            case StringArray value: writer.WriteStringValue(value.GetString(row)); break;
            case BooleanArray value: writer.WriteBooleanValue(value.GetValue(row)!.Value); break;
            case Int8Array value: writer.WriteNumberValue(value.GetValue(row)!.Value); break;
            case UInt8Array value: writer.WriteNumberValue(value.GetValue(row)!.Value); break;
            case Int16Array value: writer.WriteNumberValue(value.GetValue(row)!.Value); break;
            case UInt16Array value: writer.WriteNumberValue(value.GetValue(row)!.Value); break;
            case Int32Array value: writer.WriteNumberValue(value.GetValue(row)!.Value); break;
            case UInt32Array value: writer.WriteNumberValue(value.GetValue(row)!.Value); break;
            case Int64Array value: writer.WriteNumberValue(value.GetValue(row)!.Value); break;
            case UInt64Array value: writer.WriteNumberValue(value.GetValue(row)!.Value); break;
            case FloatArray value: writer.WriteNumberValue(value.GetValue(row)!.Value); break;
            case DoubleArray value: writer.WriteNumberValue(value.GetValue(row)!.Value); break;
            case BinaryArray value: writer.WriteBase64StringValue(value.GetBytes(row)); break;
            case NullArray: writer.WriteNullValue(); break;
            default:
                throw new NotSupportedException("GeoParquet nested attribute contains an Arrow value type that cannot yet be preserved losslessly.");
        }
    }
}
