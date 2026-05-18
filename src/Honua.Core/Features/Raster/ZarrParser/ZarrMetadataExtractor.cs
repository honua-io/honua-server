// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

/// <summary>
/// AOT-safe Zarr v2 metadata reader. Discovers arrays under a root prefix by reading
/// <c>.zgroup</c>, <c>.zattrs</c> (with an optional <c>variables</c> manifest), and
/// <c>.zarray</c> JSON documents through <see cref="ICloudRangeReader"/>.
/// </summary>
public sealed class ZarrMetadataExtractor : IZarrMetadataReader
{
    private const int MaxMetadataBytes = 64 * 1024;
    private const int MaxVariables = 64;

    /// <inheritdoc />
    public async Task<ZarrStoreMetadata> ReadMetadataAsync(
        ICloudRangeReader reader,
        string bucket,
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentNullException.ThrowIfNull(rootPath);

        var normalizedRoot = NormalizeRootPath(rootPath);
        var groupDoc = await TryReadJsonDocumentAsync(reader, bucket, JoinKey(normalizedRoot, ".zgroup"), cancellationToken)
            .ConfigureAwait(false);
        var attrsDoc = await TryReadJsonDocumentAsync(reader, bucket, JoinKey(normalizedRoot, ".zattrs"), cancellationToken)
            .ConfigureAwait(false);

        var variables = ResolveVariables(groupDoc, attrsDoc, reader, bucket, normalizedRoot);

        var arrays = new List<ZarrArrayMetadata>();
        if (variables.Count == 0)
        {
            // Treat the root itself as a single-array store.
            var rootArray = await ReadArrayAsync(
                    reader,
                    bucket,
                    arrayPath: normalizedRoot,
                    relativePath: string.Empty,
                    name: ResolveSingleName(normalizedRoot),
                    cancellationToken)
                .ConfigureAwait(false);
            arrays.Add(rootArray);
        }
        else
        {
            foreach (var name in variables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var arrayPath = JoinKey(normalizedRoot, name);
                var arr = await ReadArrayAsync(reader, bucket, arrayPath, relativePath: name, name, cancellationToken)
                    .ConfigureAwait(false);
                arrays.Add(arr);
            }
        }

        if (arrays.Count == 0)
        {
            throw new InvalidDataException("Zarr store contains no arrays.");
        }

        var (srid, extent, primary, xDim, yDim, tDim) = ResolveStoreGeoreferencing(attrsDoc, arrays);

        return new ZarrStoreMetadata(
            ZarrFormat: arrays[0].ZarrFormat,
            Srid: srid,
            Extent: extent,
            Arrays: arrays.ToArray(),
            PrimaryVariable: primary,
            SpatialXDimension: xDim,
            SpatialYDimension: yDim,
            TemporalDimension: tDim);
    }

    private static string NormalizeRootPath(string rootPath)
    {
        var trimmed = rootPath.Trim();
        while (trimmed.EndsWith('/'))
        {
            trimmed = trimmed[..^1];
        }
        return trimmed;
    }

    private static string JoinKey(string root, string segment)
    {
        if (string.IsNullOrEmpty(root))
        {
            return segment;
        }
        return root + "/" + segment;
    }

    private static string ResolveSingleName(string rootPath)
    {
        var lastSlash = rootPath.LastIndexOf('/');
        return lastSlash >= 0 && lastSlash < rootPath.Length - 1
            ? rootPath[(lastSlash + 1)..]
            : "data";
    }

    private static List<string> ResolveVariables(
        JsonDocument? groupDoc,
        JsonDocument? attrsDoc,
        ICloudRangeReader reader,
        string bucket,
        string rootPath)
    {
        _ = reader;
        _ = bucket;
        _ = rootPath;

        var variables = new List<string>();
        if (groupDoc is null)
        {
            return variables;
        }

        if (attrsDoc is not null &&
            attrsDoc.RootElement.ValueKind == JsonValueKind.Object &&
            attrsDoc.RootElement.TryGetProperty("variables", out var variablesElement) &&
            variablesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in variablesElement.EnumerateArray())
            {
                if (variables.Count >= MaxVariables)
                {
                    throw new InvalidDataException($"Zarr store exposes more than {MaxVariables} variables; reduce the manifest or split the store.");
                }
                if (entry.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var name = entry.GetString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                if (ContainsUnsafeSegment(name))
                {
                    throw new InvalidDataException($"Variable name '{name}' contains path traversal characters.");
                }
                variables.Add(name!);
            }
        }

        return variables;
    }

    private static async Task<ZarrArrayMetadata> ReadArrayAsync(
        ICloudRangeReader reader,
        string bucket,
        string arrayPath,
        string relativePath,
        string name,
        CancellationToken cancellationToken)
    {
        var zarrayDoc = await TryReadJsonDocumentAsync(reader, bucket, JoinKey(arrayPath, ".zarray"), cancellationToken)
            .ConfigureAwait(false);
        if (zarrayDoc is null)
        {
            throw new InvalidDataException($"Zarr array '{name}' is missing a .zarray document at '{arrayPath}/.zarray'.");
        }

        var arrayAttrs = await TryReadJsonDocumentAsync(reader, bucket, JoinKey(arrayPath, ".zattrs"), cancellationToken)
            .ConfigureAwait(false);

        using (zarrayDoc)
        using (arrayAttrs)
        {
            var root = zarrayDoc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Zarr array '{name}' has a malformed .zarray document.");
            }

            var format = ResolveZarrFormat(root, name);
            if (format != ZarrFormatVersion.V2)
            {
                throw new InvalidDataException($"Zarr v{(int)format} is not supported by the MVP reader; use a Zarr v2 store.");
            }

            var shape = ReadIntArray(root, "shape", name);
            var chunks = ReadIntArray(root, "chunks", name);
            if (shape.Length == 0 || shape.Length != chunks.Length)
            {
                throw new InvalidDataException($"Zarr array '{name}' has mismatched shape and chunk rank.");
            }

            foreach (var dim in shape)
            {
                if (dim <= 0)
                {
                    throw new InvalidDataException($"Zarr array '{name}' has non-positive shape entry.");
                }
            }

            foreach (var chunk in chunks)
            {
                if (chunk <= 0)
                {
                    throw new InvalidDataException($"Zarr array '{name}' has non-positive chunk entry.");
                }
            }

            var dtype = ReadRequiredString(root, "dtype", name);
            var order = root.TryGetProperty("order", out var orderEl) && orderEl.ValueKind == JsonValueKind.String
                ? orderEl.GetString() ?? "C"
                : "C";
            if (!string.Equals(order, "C", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Zarr array '{name}' uses Fortran chunk order; only C order is supported by the MVP reader.");
            }

            var compressor = ResolveCompressorId(root);

            object? fillValue = null;
            if (root.TryGetProperty("fill_value", out var fillEl))
            {
                fillValue = fillEl.ValueKind switch
                {
                    JsonValueKind.Number => fillEl.TryGetDouble(out var d) ? d : null,
                    JsonValueKind.String => fillEl.GetString(),
                    JsonValueKind.Null => null,
                    _ => null
                };
            }

            var dimNames = ReadDimensionNames(root, shape.Length);
            if (arrayAttrs is not null)
            {
                var attrDims = ReadDimensionNamesFromAttrs(arrayAttrs.RootElement, shape.Length);
                if (attrDims is not null)
                {
                    dimNames = attrDims;
                }
            }

            return new ZarrArrayMetadata(
                Name: name,
                ZarrFormat: format,
                RelativePath: relativePath,
                Shape: shape,
                Chunks: chunks,
                DataType: dtype,
                Order: order,
                Compressor: compressor,
                FillValue: fillValue,
                DimensionNames: dimNames);
        }
    }

    private static ZarrFormatVersion ResolveZarrFormat(JsonElement root, string arrayName)
    {
        if (!root.TryGetProperty("zarr_format", out var formatEl))
        {
            throw new InvalidDataException($"Zarr array '{arrayName}' is missing the zarr_format property.");
        }

        if (formatEl.ValueKind != JsonValueKind.Number || !formatEl.TryGetInt32(out var formatNumber))
        {
            throw new InvalidDataException($"Zarr array '{arrayName}' has a non-numeric zarr_format property.");
        }

        return formatNumber switch
        {
            2 => ZarrFormatVersion.V2,
            3 => ZarrFormatVersion.V3,
            _ => throw new InvalidDataException($"Zarr array '{arrayName}' declares unsupported zarr_format '{formatNumber}'.")
        };
    }

    private static int[] ReadIntArray(JsonElement root, string property, string arrayName)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Zarr array '{arrayName}' is missing required property '{property}'.");
        }

        var values = new List<int>();
        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Number || !entry.TryGetInt32(out var value))
            {
                throw new InvalidDataException($"Zarr array '{arrayName}' property '{property}' contains a non-integer entry.");
            }
            values.Add(value);
        }
        return values.ToArray();
    }

    private static string ReadRequiredString(JsonElement root, string property, string arrayName)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Zarr array '{arrayName}' is missing required string property '{property}'.");
        }
        return element.GetString() ?? throw new InvalidDataException($"Zarr array '{arrayName}' has empty '{property}'.");
    }

    private static string? ResolveCompressorId(JsonElement root)
    {
        if (!root.TryGetProperty("compressor", out var element))
        {
            return null;
        }
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("id", out var idEl) &&
            idEl.ValueKind == JsonValueKind.String)
        {
            return idEl.GetString();
        }
        return null;
    }

    private static string[]? ReadDimensionNamesFromAttrs(JsonElement root, int rank)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("_ARRAY_DIMENSIONS", out var dimsEl) ||
            dimsEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = new List<string>();
        foreach (var entry in dimsEl.EnumerateArray())
        {
            names.Add(entry.ValueKind == JsonValueKind.String ? entry.GetString() ?? string.Empty : string.Empty);
        }
        return names.Count == rank ? names.ToArray() : null;
    }

    private static string[] ReadDimensionNames(JsonElement root, int rank)
    {
        if (root.TryGetProperty("_ARRAY_DIMENSIONS", out var dimsEl) && dimsEl.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var entry in dimsEl.EnumerateArray())
            {
                names.Add(entry.ValueKind == JsonValueKind.String ? entry.GetString() ?? string.Empty : string.Empty);
            }
            if (names.Count == rank)
            {
                return names.ToArray();
            }
        }

        var fallback = new string[rank];
        for (var i = 0; i < rank; i++)
        {
            fallback[i] = "dim_" + i.ToString(CultureInfo.InvariantCulture);
        }
        return fallback;
    }

    private static (int Srid, RasterExtent Extent, string? PrimaryVariable, string? XDim, string? YDim, string? TDim)
        ResolveStoreGeoreferencing(JsonDocument? attrsDoc, List<ZarrArrayMetadata> arrays)
    {
        var srid = 0;
        double xMin = 0, yMin = 0, xMax = 0, yMax = 0;
        bool hasExtent = false;
        string? primary = null;
        string? xDim = null;
        string? yDim = null;
        string? tDim = null;

        if (attrsDoc is not null && attrsDoc.RootElement.ValueKind == JsonValueKind.Object)
        {
            var root = attrsDoc.RootElement;
            if (root.TryGetProperty("crs_wkid", out var crsEl) && crsEl.ValueKind == JsonValueKind.Number && crsEl.TryGetInt32(out var crsValue))
            {
                srid = crsValue;
            }
            else if (root.TryGetProperty("crs", out var crsStringEl) && crsStringEl.ValueKind == JsonValueKind.String)
            {
                if (TryParseEpsg(crsStringEl.GetString(), out var parsedSrid))
                {
                    srid = parsedSrid;
                }
            }

            if (root.TryGetProperty("extent", out var extentEl) && extentEl.ValueKind == JsonValueKind.Array)
            {
                var coords = new List<double>();
                foreach (var entry in extentEl.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Number && entry.TryGetDouble(out var d))
                    {
                        coords.Add(d);
                    }
                }
                if (coords.Count == 4)
                {
                    xMin = coords[0];
                    yMin = coords[1];
                    xMax = coords[2];
                    yMax = coords[3];
                    hasExtent = true;
                }
            }

            primary = ReadOptionalString(root, "primary_variable");
            xDim = ReadOptionalString(root, "x_dimension");
            yDim = ReadOptionalString(root, "y_dimension");
            tDim = ReadOptionalString(root, "t_dimension");
        }

        if (string.IsNullOrEmpty(primary) && arrays.Count > 0)
        {
            primary = arrays[0].Name;
        }

        var extent = new RasterExtent
        {
            XMin = hasExtent ? xMin : 0,
            YMin = hasExtent ? yMin : 0,
            XMax = hasExtent ? xMax : (arrays.Count > 0 && arrays[0].Shape.Length >= 1 ? arrays[0].Shape[^1] : 0),
            YMax = hasExtent ? yMax : (arrays.Count > 0 && arrays[0].Shape.Length >= 2 ? arrays[0].Shape[^2] : 0),
            Srid = srid
        };

        return (srid, extent, primary, xDim, yDim, tDim);
    }

    private static string? ReadOptionalString(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    private static bool TryParseEpsg(string? value, out int srid)
    {
        srid = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        const string prefix = "EPSG:";
        var span = value.AsSpan().Trim();
        if (span.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            span = span[prefix.Length..];
        }
        return int.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid) && srid > 0;
    }

    internal static async Task<JsonDocument?> TryReadJsonDocumentAsync(
        ICloudRangeReader reader,
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        byte[] payload;
        try
        {
            payload = await reader.ReadRangeAsync(bucket, key, 0, MaxMetadataBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        if (payload.Length == 0)
        {
            return null;
        }

        var byteCount = payload.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Buffer.BlockCopy(payload, 0, buffer, 0, byteCount);
            // Ensure UTF-8 with no BOM consideration; Zarr metadata is JSON.
            var text = Encoding.UTF8.GetString(buffer, 0, byteCount);
            try
            {
                return JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                throw new InvalidDataException($"Zarr metadata at '{key}' is not valid JSON.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static bool ContainsUnsafeSegment(string value)
        => value.Contains("..", StringComparison.Ordinal) ||
           value.Contains('\\', StringComparison.Ordinal) ||
           value.StartsWith('/');
}
