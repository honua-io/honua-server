// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Raster.ZarrParser;

public sealed record ZarrSupportCell
{
    public required int Version { get; init; }
    public required string DataType { get; init; }
    public required string NormalizedDataType { get; init; }
    public required int ElementBytes { get; init; }
    public string Order => "C";
    public required string ChunkKeyEncoding { get; init; }
    public required string Separator { get; init; }
    public required string Codec { get; init; }
    public string? Endian { get; init; }
    public bool IsBoolean => NormalizedDataType.EndsWith("b1", StringComparison.Ordinal);
}

public static class ZarrSupportMatrix
{
    public const string Format = "honua.zarr-support/v1";
    public const string ReasonCodeKey = "zarrReasonCode";
    public const string UnsupportedDataType = "zarr.dtype.unsupported";
    public const string UnsupportedEndian = "zarr.endian.unsupported";
    public const string UnsupportedOrder = "zarr.order.unsupported";
    public const string UnsupportedChunkKey = "zarr.chunk-key.unsupported";
    public const string UnsupportedCodec = "zarr.codec.unsupported";
    public const string UnsupportedPipeline = "zarr.codec-pipeline.unsupported";
    public const string UnsupportedFilter = "zarr.filter.unsupported";

    private sealed record Scalar(string Name, string Dtype, int Bytes);

    private static readonly Scalar[] Scalars =
    [
        new("bool", "|b1", 1), new("int8", "|i1", 1), new("uint8", "|u1", 1),
        new("int16", "<i2", 2), new("uint16", "<u2", 2),
        new("int32", "<i4", 4), new("uint32", "<u4", 4),
        new("int64", "<i8", 8), new("uint64", "<u8", 8),
        new("float32", "<f4", 4), new("float64", "<f8", 8)
    ];

    public static IReadOnlyList<string> RejectionCodes { get; } = Array.AsReadOnly(new[]
    {
        UnsupportedDataType, UnsupportedEndian, UnsupportedOrder, UnsupportedChunkKey,
        UnsupportedCodec, UnsupportedPipeline, UnsupportedFilter
    });

    public static IReadOnlyList<ZarrSupportCell> Cells { get; } = Array.AsReadOnly(BuildCells().ToArray());

    internal static InvalidDataException Reject(string reason, string message)
    {
        var error = new InvalidDataException(message);
        error.Data[ReasonCodeKey] = reason;
        return error;
    }

    internal static int ResolveElementSize(string dtype)
    {
        if (dtype.Length > 0 && dtype[0] == '>')
            throw Reject(UnsupportedEndian, $"Zarr dtype '{dtype}' is big-endian; only little-endian dtypes are supported.");
        var scalar = dtype.Length >= 3 && dtype[0] is '<' or '|' or '='
            ? Scalars.FirstOrDefault(value => value.Dtype.AsSpan(1).SequenceEqual(dtype.AsSpan(1)))
            : null;
        return scalar?.Bytes ?? throw Reject(UnsupportedDataType, $"Zarr dtype '{dtype}' is not supported by the scalar reader.");
    }

    internal static string NormalizeV3DataType(string dtype, string name)
        => Scalars.FirstOrDefault(value => value.Name == dtype)?.Dtype
            ?? throw Reject(UnsupportedDataType, $"Zarr v3 array '{name}' uses unsupported data_type '{dtype}'.");

    internal static void ValidateCodec(string? codec, int version)
    {
        if (codec is null or "gzip" || version == 2 && codec == "zlib") return;
        throw Reject(UnsupportedCodec, $"Zarr v{version} compressor '{codec}' is not supported; use uncompressed or supported DEFLATE chunks.");
    }

    private static IEnumerable<ZarrSupportCell> BuildCells()
        => Scalars.SelectMany(scalar => BuildV2Cells(scalar).Concat(BuildV3Cells(scalar)));

    private static IEnumerable<ZarrSupportCell> BuildV2Cells(Scalar scalar)
        => from prefix in new[] { "<", "|", "=" }
           from separator in new[] { ".", "/" }
           from codec in new[] { "none", "zlib", "gzip" }
           select CreateCell(scalar, 2, prefix + scalar.Dtype[1..]) with
           { ChunkKeyEncoding = "v2", Separator = separator, Codec = codec };

    private static IEnumerable<ZarrSupportCell> BuildV3Cells(Scalar scalar)
        => from encoding in new[] { "default", "v2" }
           from separator in new[] { "/", "." }
           from codec in new[] { "none", "gzip" }
           from endian in (scalar.Bytes == 1 ? new string?[] { "little", "big", null } : new string?[] { "little" })
           select CreateCell(scalar, 3, scalar.Name) with
           { ChunkKeyEncoding = encoding, Separator = separator, Codec = codec, Endian = endian };

    private static ZarrSupportCell CreateCell(Scalar scalar, int version, string dtype) => new()
    {
        Version = version, DataType = dtype, NormalizedDataType = scalar.Dtype, ElementBytes = scalar.Bytes,
        ChunkKeyEncoding = "v2", Separator = ".", Codec = "none"
    };

    public static void WriteJson(Stream output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString("format", Format);
        writer.WriteString("scope", "Coverage subset and scalar decoding; C order, regular nonempty arrays, no filters. Native/neutral v2 prefixes use little-endian bytes.");
        writer.WriteStartArray("cells");
        foreach (var cell in Cells) WriteCell(writer, cell);
        writer.WriteEndArray();
        writer.WriteStartArray("rejectionCodes");
        foreach (var reason in RejectionCodes) writer.WriteStringValue(reason);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteCell(Utf8JsonWriter writer, ZarrSupportCell cell)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", cell.Version);
        writer.WriteString("dataType", cell.DataType);
        writer.WriteString("normalizedDataType", cell.NormalizedDataType);
        writer.WriteNumber("elementBytes", cell.ElementBytes);
        writer.WriteString("order", cell.Order);
        writer.WriteString("chunkKeyEncoding", cell.ChunkKeyEncoding);
        writer.WriteString("separator", cell.Separator);
        writer.WriteString("codec", cell.Codec);
        writer.WriteString("endian", cell.Endian);
        writer.WriteEndObject();
    }
}
