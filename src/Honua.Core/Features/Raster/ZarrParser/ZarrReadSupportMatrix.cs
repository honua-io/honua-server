// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.ObjectModel;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

/// <summary>
/// One executable row in the managed Zarr direct-read support matrix.
/// </summary>
internal sealed record ZarrReadSupportEntry(
    ZarrFormatVersion Version,
    string Axis,
    string Value,
    bool Supported,
    string Constraint);

/// <summary>
/// The authoritative envelope for Zarr data that may enter Honua's pure-managed,
/// AOT-safe web read path. Parser admission and fixture tests consume the same
/// rules so metadata discovery cannot advertise data that downstream slice/value/
/// tile readers cannot decode.
/// </summary>
internal static class ZarrReadSupportMatrix
{
    internal const int MaxMetadataDocumentBytes = 64 * 1024;
    internal const int MaxVariables = 64;

    private static readonly DataTypeSupport[] DataTypes =
    [
        new("|b1", "bool", 1),
        new("|i1", "int8", 1),
        new("|u1", "uint8", 1),
        new("<i2", "int16", 2),
        new("<u2", "uint16", 2),
        new("<i4", "int32", 4),
        new("<u4", "uint32", 4),
        new("<i8", "int64", 8),
        new("<u8", "uint64", 8),
        new("<f4", "float32", 4),
        new("<f8", "float64", 8),
    ];

    /// <summary>
    /// Complete human-readable matrix rows. These rows are also asserted by tests;
    /// the tailored helpers below are used on the parser's hot path.
    /// </summary>
    internal static IReadOnlyList<ZarrReadSupportEntry> Entries { get; } = BuildEntries();

    internal static bool TryNormalizeDataType(
        ZarrFormatVersion version,
        string dataType,
        out string normalized,
        out int elementSize)
    {
        ArgumentNullException.ThrowIfNull(dataType);

        foreach (var candidate in DataTypes)
        {
            var advertised = version == ZarrFormatVersion.V2
                ? candidate.V2Name
                : candidate.V3Name;
            if (!string.Equals(advertised, dataType, StringComparison.Ordinal))
            {
                continue;
            }

            normalized = candidate.V2Name;
            elementSize = candidate.ElementSize;
            return true;
        }

        normalized = string.Empty;
        elementSize = 0;
        return false;
    }

    internal static bool TryGetElementSize(string normalizedDataType, out int elementSize)
    {
        ArgumentNullException.ThrowIfNull(normalizedDataType);

        foreach (var candidate in DataTypes)
        {
            if (string.Equals(candidate.V2Name, normalizedDataType, StringComparison.Ordinal))
            {
                elementSize = candidate.ElementSize;
                return true;
            }
        }

        elementSize = 0;
        return false;
    }

    internal static bool IsOrderSupported(string order)
        => string.Equals(order, "C", StringComparison.Ordinal);

    internal static bool IsV2CompressorSupported(string? compressor)
        => compressor is null || string.Equals(compressor, "zlib", StringComparison.Ordinal);

    internal static bool IsV3CodecSupported(string codec)
        => codec is "bytes" or "gzip";

    internal static bool IsV3EndianSupported(string? endian)
        => string.Equals(endian, "little", StringComparison.Ordinal);

    internal static bool IsChunkSeparatorSupported(string? separator)
        => separator is "." or "/";

    internal static bool IsV3ChunkKeyEncodingSupported(string? encoding)
        => encoding is "default" or "v2";

    private static ReadOnlyCollection<ZarrReadSupportEntry> BuildEntries()
    {
        var entries = new List<ZarrReadSupportEntry>
        {
            new(ZarrFormatVersion.V2, "format", "Zarr v2", true, "Root array or manifest-backed group."),
            new(ZarrFormatVersion.V3, "format", "Zarr v3", true, "Root array or manifest-backed group."),

            new(ZarrFormatVersion.V2, "codec", "none", true, "Raw C-order chunk bytes."),
            new(ZarrFormatVersion.V2, "codec", "zlib", true, "RFC 1950 zlib framing."),
            new(ZarrFormatVersion.V2, "codec", "other", false, "Filters and other numcodecs require GP conversion."),
            new(ZarrFormatVersion.V3, "codec", "bytes", true, "Required first codec with explicit little endianness."),
            new(ZarrFormatVersion.V3, "codec", "gzip", true, "Optional sole compression codec after bytes."),
            new(ZarrFormatVersion.V3, "codec", "other", false, "Including zstd, blosc, sharding_indexed, and crc32c."),

            new(ZarrFormatVersion.V2, "endian", "little / one-byte not-applicable", true, "Explicit '<' for multi-byte and '|' for one-byte values."),
            new(ZarrFormatVersion.V2, "endian", "big / native", false, "No byte swapping or platform-native interpretation in web."),
            new(ZarrFormatVersion.V3, "endian", "little", true, "Explicit bytes-codec configuration."),
            new(ZarrFormatVersion.V3, "endian", "big / missing / native", false, "Fail closed before chunk reads."),

            new(ZarrFormatVersion.V2, "order", "C", true, "Row-major chunk layout."),
            new(ZarrFormatVersion.V2, "order", "F", false, "Requires conversion."),
            new(ZarrFormatVersion.V3, "order", "C", true, "Managed bytes-codec layout."),
            new(ZarrFormatVersion.V3, "order", "other", false, "Requires conversion."),

            new(ZarrFormatVersion.V2, "dimensions", "rank >= 1", true, "Positive Int32 shape/chunk entries with equal rank."),
            new(ZarrFormatVersion.V2, "dimensions", "scalar/non-positive/mismatched", false, "Rejected during metadata admission."),
            new(ZarrFormatVersion.V3, "dimensions", "rank >= 1", true, "Positive Int32 regular chunk grid with equal rank."),
            new(ZarrFormatVersion.V3, "dimensions", "scalar/non-positive/mismatched/irregular", false, "Rejected during metadata admission."),

            new(ZarrFormatVersion.V2, "metadata", "root .zarray", true, $"Each metadata document <= {MaxMetadataDocumentBytes} bytes."),
            new(ZarrFormatVersion.V2, "metadata", "group .zgroup + .zattrs variables manifest", true, $"At most {MaxVariables} declared variables."),
            new(ZarrFormatVersion.V2, "metadata", ".zmetadata or unlisted hierarchy discovery", false, "No provider prefix scan in the web request path."),
            new(ZarrFormatVersion.V3, "metadata", "root zarr.json array", true, $"Each metadata document <= {MaxMetadataDocumentBytes} bytes."),
            new(ZarrFormatVersion.V3, "metadata", "group zarr.json attributes.variables manifest", true, $"At most {MaxVariables} declared variables."),
            new(ZarrFormatVersion.V3, "metadata", "unlisted hierarchy discovery", false, "No provider prefix scan in the web request path."),

            new(ZarrFormatVersion.V2, "chunk-key", "dimension_separator '.' or '/'", true, "No chunk-key prefix."),
            new(ZarrFormatVersion.V2, "chunk-key", "other", false, "Rejected during metadata admission."),
            new(ZarrFormatVersion.V3, "chunk-key", "default or v2; separator '.' or '/'", true, "Default uses the c prefix; v2 has no prefix."),
            new(ZarrFormatVersion.V3, "chunk-key", "other", false, "Rejected during metadata admission."),
        };

        foreach (var dataType in DataTypes)
        {
            entries.Add(new ZarrReadSupportEntry(
                ZarrFormatVersion.V2,
                "dtype",
                dataType.V2Name,
                true,
                $"{dataType.ElementSize}-byte value supported by subset, scalar, and tile decoders."));
            entries.Add(new ZarrReadSupportEntry(
                ZarrFormatVersion.V3,
                "dtype",
                dataType.V3Name,
                true,
                $"Normalizes to {dataType.V2Name}."));
        }

        entries.Add(new ZarrReadSupportEntry(
            ZarrFormatVersion.V2,
            "dtype",
            "other (including big/native endian, float16, complex, strings, objects)",
            false,
            "Requires GP conversion."));
        entries.Add(new ZarrReadSupportEntry(
            ZarrFormatVersion.V3,
            "dtype",
            "other (including float16, complex, strings, objects)",
            false,
            "Requires GP conversion."));

        return entries.AsReadOnly();
    }

    private sealed record DataTypeSupport(string V2Name, string V3Name, int ElementSize);
}
