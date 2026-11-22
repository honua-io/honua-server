// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Honua.Infrastructure.Services;

public static partial class GeoParquetFeatureWriter
{
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "RecordBatch owns the arrays after successful construction. Partial construction disposes each created array.")]
    private static RecordBatch CopyToUpgradedSchema(
        RecordBatch source, Schema targetSchema, Dictionary<string, int> sourceIndex)
    {
        // Copy C-imported reader columns into fresh managed Arrow arrays so CArrowArrayExporter
        // never re-exports native buffers owned by the reader's importer handle.
        var arrays = new IArrowArray[targetSchema.FieldsList.Count];
        var created = 0;
        try
        {
            for (var i = 0; i < targetSchema.FieldsList.Count; i++)
            {
                var field = targetSchema.FieldsList[i];
                arrays[i] = sourceIndex.TryGetValue(field.Name, out var oldIndex)
                    ? CloneToManagedArray(source.Column(oldIndex))
                    : BuildNullArray(field.DataType, source.Length);
                created++;
            }

            return new RecordBatch(targetSchema, arrays, source.Length);
        }
        catch
        {
            for (var i = 0; i < created; i++)
            {
                arrays[i].Dispose();
            }

            throw;
        }
    }

    private static IArrowArray CloneToManagedArray(IArrowArray column) => column switch
    {
        Int64Array a => ClonePrimitive(a, new Int64Array.Builder()),
        Int32Array a => ClonePrimitive(a, new Int32Array.Builder()),
        Int16Array a => ClonePrimitive(a, new Int16Array.Builder()),
        DoubleArray a => ClonePrimitive(a, new DoubleArray.Builder()),
        FloatArray a => ClonePrimitive(a, new FloatArray.Builder()),
        BooleanArray a => CloneBoolean(a),
        StringArray a => CloneString(a),
        BinaryArray a => CloneBinary(a),
        Date32Array a => CloneDate32(a),
        TimestampArray a => CloneTimestamp(a),
        Time64Array a => CloneTime64(a),
        StructArray a => CloneStruct(a),
        _ => throw new NotSupportedException($"Unsupported Arrow column type '{column.Data.DataType.Name}' during schema rewrite.")
    };

    private static TArray ClonePrimitive<T, TArray, TBuilder>(
        PrimitiveArray<T> source,
        PrimitiveArrayBuilder<T, TArray, TBuilder> builder)
        where T : struct, IEquatable<T>
        where TArray : IArrowArray
        where TBuilder : PrimitiveArrayBuilder<T, TArray, TBuilder>
    {
        for (var i = 0; i < source.Length; i++)
        {
            if (source.GetValue(i) is { } value)
            {
                builder.Append(value);
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build();
    }

    private static BooleanArray CloneBoolean(BooleanArray source)
    {
        var builder = new BooleanArray.Builder();
        for (var i = 0; i < source.Length; i++)
        {
            if (source.GetValue(i) is { } value)
            {
                builder.Append(value);
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build();
    }

    private static StringArray CloneString(StringArray source)
    {
        var builder = new StringArray.Builder();
        for (var i = 0; i < source.Length; i++)
        {
            if (source.IsNull(i))
            {
                builder.AppendNull();
            }
            else
            {
                builder.Append(source.GetString(i));
            }
        }

        return builder.Build();
    }

    private static BinaryArray CloneBinary(BinaryArray source)
    {
        var builder = new BinaryArray.Builder();
        for (var i = 0; i < source.Length; i++)
        {
            if (source.IsNull(i))
            {
                builder.AppendNull();
            }
            else
            {
                builder.Append(source.GetBytes(i));
            }
        }

        return builder.Build();
    }

    private static Date32Array CloneDate32(Date32Array source)
    {
        var builder = new Date32Array.Builder();
        for (var i = 0; i < source.Length; i++)
        {
            if (source.GetDateOnly(i) is { } value)
            {
                builder.Append(value);
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build();
    }

    private static TimestampArray CloneTimestamp(TimestampArray source)
    {
        var timestampType = (TimestampType)source.Data.DataType;
        var builder = new TimestampArray.Builder(timestampType);
        for (var i = 0; i < source.Length; i++)
        {
            if (source.GetTimestamp(i) is { } value)
            {
                builder.Append(value);
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build();
    }

    private static Time64Array CloneTime64(Time64Array source)
    {
        var builder = new Int64Array.Builder();
        for (var i = 0; i < source.Length; i++)
        {
            if (source.GetValue(i) is { } value)
            {
                builder.Append(value);
            }
            else
            {
                builder.AppendNull();
            }
        }

        var int64Array = builder.Build();
        return new Time64Array(
            (Time64Type)source.Data.DataType,
            int64Array.ValueBuffer,
            int64Array.NullBitmapBuffer,
            int64Array.Length,
            int64Array.NullCount,
            int64Array.Offset);
    }

    private static StructArray CloneStruct(StructArray source)
    {
        var structType = (StructType)source.Data.DataType;
        var fields = new IArrowArray[source.Fields.Count];
        var bitmap = new ArrowBuffer.BitmapBuilder();
        for (var i = 0; i < source.Fields.Count; i++)
        {
            fields[i] = CloneToManagedArray(source.Fields[i]);
        }

        for (var i = 0; i < source.Length; i++)
        {
            bitmap.Append(!source.IsNull(i));
        }

        return new StructArray(structType, source.Length, fields, bitmap.Build(), source.NullCount);
    }

    private static IArrowArray BuildNullArray(IArrowType dataType, int length) => dataType switch
    {
        DoubleType => FillNulls(new DoubleArray.Builder(), length),
        FloatType => FillNulls(new FloatArray.Builder(), length),
        Int32Type => FillNulls(new Int32Array.Builder(), length),
        Int64Type => FillNulls(new Int64Array.Builder(), length),
        BooleanType => FillNulls(new BooleanArray.Builder(), length),
        Date32Type => FillNulls(new Date32Array.Builder(), length),
        Time64Type => BuildNullTime64(length),
        TimestampType => FillNulls(new TimestampArray.Builder(TimeUnit.Microsecond, TimeZoneInfo.Utc), length),
        StringType => FillNulls(new StringArray.Builder(), length),
        _ => throw new NotSupportedException($"Unsupported new Arrow attribute type '{dataType.Name}' during schema rewrite.")
    };

    private static TArray FillNulls<T, TArray, TBuilder>(
        PrimitiveArrayBuilder<T, TArray, TBuilder> builder, int length)
        where T : struct, IEquatable<T>
        where TArray : IArrowArray
        where TBuilder : PrimitiveArrayBuilder<T, TArray, TBuilder>
    {
        for (var i = 0; i < length; i++)
        {
            builder.AppendNull();
        }

        return builder.Build();
    }

    private static BooleanArray FillNulls(BooleanArray.Builder builder, int length)
    {
        for (var i = 0; i < length; i++)
        {
            builder.AppendNull();
        }

        return builder.Build();
    }

    private static StringArray FillNulls(StringArray.Builder builder, int length)
    {
        for (var i = 0; i < length; i++)
        {
            builder.AppendNull();
        }

        return builder.Build();
    }

    private static Date32Array FillNulls(Date32Array.Builder builder, int length)
    {
        for (var i = 0; i < length; i++)
        {
            builder.AppendNull();
        }

        return builder.Build();
    }

    private static TimestampArray FillNulls(TimestampArray.Builder builder, int length)
    {
        for (var i = 0; i < length; i++)
        {
            builder.AppendNull();
        }

        return builder.Build();
    }

    private static Time64Array BuildNullTime64(int length)
    {
        var builder = new Int64Array.Builder();
        for (var i = 0; i < length; i++)
        {
            builder.AppendNull();
        }

        var int64Array = builder.Build();
        return new Time64Array(
            new Time64Type(TimeUnit.Microsecond),
            int64Array.ValueBuffer,
            int64Array.NullBitmapBuffer,
            int64Array.Length,
            int64Array.NullCount,
            int64Array.Offset);
    }
}
