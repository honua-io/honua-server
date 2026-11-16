// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Globalization;

namespace Honua.Worker.Gdal.Execution;

internal static class GdalCalcInputs
{
    public const string SourceSeparator = "|";

    private static readonly FrozenDictionary<string, string> DataTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["byte"] = "Byte",
            ["int16"] = "Int16",
            ["uint16"] = "UInt16",
            ["int32"] = "Int32",
            ["uint32"] = "UInt32",
            ["float32"] = "Float32",
            ["float64"] = "Float64",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static bool TryDecodeSources(
        IReadOnlyDictionary<string, string> parameters,
        long maxBytes,
        out List<byte[]> sources,
        out string failure)
    {
        sources = [];
        failure = "";

        if (!GdalJobInputReader.TryGetInput(parameters, "sources", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            failure = "missing required input 'sources'";
            return false;
        }

        var entries = raw.Split(SourceSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0)
        {
            failure = $"'sources' must list at least one base64 GeoTIFF raster (separator '{SourceSeparator}')";
            return false;
        }

        if (entries.Length > 26)
        {
            failure = $"'sources' must list at most 26 rasters (one per band variable A–Z); got {entries.Length.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        var decoded = new List<byte[]>(entries.Length);
        long totalBytes = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if ((long)entry.Length / 4 * 3 > maxBytes)
            {
                failure = $"source #{(i + 1).ToString(CultureInfo.InvariantCulture)} exceeds configured MaxArtifactBytes={maxBytes}";
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(entry);
            }
            catch (FormatException)
            {
                failure = $"source #{(i + 1).ToString(CultureInfo.InvariantCulture)} is not valid base64";
                return false;
            }

            if (bytes.Length == 0)
            {
                failure = $"source #{(i + 1).ToString(CultureInfo.InvariantCulture)} decoded to zero bytes";
                return false;
            }

            // Refuse a content-sniffed VRT/XML indirection blob or an embedded /vsi
            // reference before it is ever materialized and opened by GDAL.
            if (!GdalUntrustedInputGuard.IsAdmissible(bytes, out var guardReason))
            {
                failure = $"source #{(i + 1).ToString(CultureInfo.InvariantCulture)} rejected: {guardReason}";
                return false;
            }

            if (bytes.Length > maxBytes)
            {
                failure = $"source #{(i + 1).ToString(CultureInfo.InvariantCulture)} size {bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes exceeds configured MaxArtifactBytes={maxBytes}";
                return false;
            }

            // The decoder buffers every source in memory simultaneously, so bound the
            // aggregate decoded size by the same MaxArtifactBytes ceiling used for the
            // single output artifact rather than letting N×per-source slip through.
            totalBytes += bytes.Length;
            if (totalBytes > maxBytes)
            {
                failure = $"decoded sources total {totalBytes.ToString(CultureInfo.InvariantCulture)} bytes, exceeding configured MaxArtifactBytes={maxBytes}";
                return false;
            }

            decoded.Add(bytes);
        }

        sources = decoded;
        return true;
    }

    public static bool TryNormalizeDataType(string raw, out string? normalized, out string failure)
    {
        normalized = null;
        failure = "";
        if (!DataTypes.TryGetValue(raw.Trim(), out var mapped))
        {
            failure = $"'dataType' value '{raw}' is not in the allowed set (Byte, Int16, UInt16, Int32, UInt32, Float32, Float64)";
            return false;
        }

        normalized = mapped;
        return true;
    }
}
