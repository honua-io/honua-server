// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.Multidimensional.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

/// <summary>
/// AOT-safe Zarr metadata reader. Reads Zarr v2 stores via <c>.zgroup</c>,
/// <c>.zattrs</c> (with an optional <c>variables</c> manifest), and <c>.zarray</c>
/// documents, and Zarr v3 stores via consolidated <c>zarr.json</c> documents
/// (<c>node_type</c> group/array), all through <see cref="ICloudRangeReader"/>.
/// </summary>
public sealed class ZarrMetadataExtractor : IZarrMetadataReader
{
    // Consolidated metadata and CF coordinate manifests routinely exceed 64 KiB.
    // Keep a bounded document ceiling, but do not mistake a provider's partial
    // range response for a complete JSON document.
    private const int MaxMetadataBytes = 16 * 1024 * 1024;
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

        // Zarr v3 stores keep all node metadata in `zarr.json`. Detect it at the
        // root before falling back to the v2 `.zgroup`/`.zarray` layout.
        var v3RootDoc = await TryReadJsonDocumentAsync(reader, bucket, JoinKey(normalizedRoot, "zarr.json"), cancellationToken)
            .ConfigureAwait(false);
        if (v3RootDoc is not null)
        {
            using (v3RootDoc)
            {
                return await ReadV3MetadataAsync(reader, bucket, normalizedRoot, v3RootDoc, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var groupDoc = await TryReadJsonDocumentAsync(reader, bucket, JoinKey(normalizedRoot, ".zgroup"), cancellationToken)
            .ConfigureAwait(false);
        var attrsDoc = await TryReadJsonDocumentAsync(reader, bucket, JoinKey(normalizedRoot, ".zattrs"), cancellationToken)
            .ConfigureAwait(false);
        using var consolidatedDoc = await TryReadJsonDocumentAsync(
                reader, bucket, JoinKey(normalizedRoot, ".zmetadata"), cancellationToken)
            .ConfigureAwait(false);

        var variables = ResolveVariables(groupDoc, attrsDoc, consolidatedDoc);

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

        var (srid, extent, primary, xDim, yDim, tDim, temporal, axes) =
            ResolveStoreGeoreferencing(attrsDoc?.RootElement, arrays);

        return new ZarrStoreMetadata(
            ZarrFormat: arrays[0].ZarrFormat,
            Srid: srid,
            Extent: extent,
            Arrays: arrays.ToArray(),
            PrimaryVariable: primary,
            SpatialXDimension: xDim,
            SpatialYDimension: yDim,
            TemporalDimension: tDim,
            Temporal: temporal,
            Axes: axes);
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
        JsonDocument? consolidatedDoc)
    {
        var variables = new List<string>();
        if (groupDoc is null)
        {
            return variables;
        }
        var group = groupDoc.RootElement;
        if (group.ValueKind != JsonValueKind.Object ||
            !group.TryGetProperty("zarr_format", out var groupVersion) ||
            groupVersion.ValueKind != JsonValueKind.Number || !groupVersion.TryGetInt32(out var groupFormat) || groupFormat != 2 ||
            group.EnumerateObject().Any(property => property.Name != "zarr_format"))
        {
            throw new InvalidDataException("Zarr v2 group metadata must contain only integer zarr_format 2.");
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

        if (variables.Count == 0 &&
            consolidatedDoc?.RootElement.TryGetProperty("metadata", out var metadata) == true &&
            metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                const string suffix = "/.zarray";
                if (!property.Name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    continue;
                }

                var name = property.Name[..^suffix.Length];
                if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || ContainsUnsafeSegment(name))
                {
                    continue;
                }

                if (variables.Count >= MaxVariables)
                {
                    throw new InvalidDataException($"Zarr store exposes more than {MaxVariables} variables; reduce the manifest or split the store.");
                }

                variables.Add(name);
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

            foreach (var field in new[] { "zarr_format", "shape", "chunks", "dtype", "compressor", "fill_value", "order", "filters" })
            {
                if (!root.TryGetProperty(field, out _))
                    throw new InvalidDataException($"Zarr v2 array '{name}' is missing required property '{field}'.");
            }
            var format = ResolveZarrFormat(root, name);
            if (format != ZarrFormatVersion.V2)
            {
                throw new InvalidDataException($"Zarr v{(int)format} is not supported by the MVP reader; use a Zarr v2 store.");
            }

            var shape = ReadIntArray(root, "shape", name);
            var chunks = ReadIntArray(root, "chunks", name);
            if (shape.Length != chunks.Length)
            {
                throw new InvalidDataException($"Zarr array '{name}' has mismatched shape and chunk rank.");
            }
            if (shape.Length == 0)
            {
                throw new InvalidDataException($"Scalar Zarr v2 arrays such as '{name}' are not supported for coverage serving.");
            }
            if (shape.Any(dim => dim < 0))
            {
                throw new InvalidDataException($"Zarr array '{name}' has a negative shape entry.");
            }
            if (shape.Contains(0))
            {
                throw new InvalidDataException($"Empty Zarr v2 dimensions in array '{name}' are not supported for coverage serving.");
            }

            if (chunks.Any(chunk => chunk <= 0))
            {
                throw new InvalidDataException($"Zarr array '{name}' has non-positive chunk entry.");
            }

            var dtype = ReadRequiredString(root, "dtype", name);
            var order = ReadRequiredString(root, "order", name);
            if (order is not ("C" or "F"))
            {
                throw new InvalidDataException($"Zarr array '{name}' has invalid chunk order '{order}'.");
            }
            if (!string.Equals(order, "C", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Zarr array '{name}' uses Fortran chunk order; only C order is supported by the MVP reader.");
            }

            RejectUnsupportedFilters(root, name);

            var compressor = ResolveCompressorId(root, name);

            var fillValue = ZarrFillValueCodec.Parse(root.GetProperty("fill_value"), dtype, name, v3: false);

            var dimensionSeparator = ".";
            if (root.TryGetProperty("dimension_separator", out var separatorEl))
            {
                if (separatorEl.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException($"Zarr array '{name}' has a malformed dimension_separator.");
                }

                dimensionSeparator = separatorEl.GetString() ?? string.Empty;
                if (dimensionSeparator is not ("." or "/"))
                {
                    throw new InvalidDataException($"Zarr array '{name}' uses unsupported dimension_separator '{dimensionSeparator}'.");
                }
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
                DimensionNames: dimNames,
                ChunkKeyEncoding: new ZarrChunkKeyEncoding(dimensionSeparator, string.Empty, "0"));
        }
    }

    // ---- Zarr v3 (zarr.json) ----------------------------------------------

    /// <summary>
    /// Reads a Zarr v3 store rooted at a <c>zarr.json</c> document. The root may be
    /// a v3 group (variables discovered from a <c>variables</c> attribute manifest)
    /// or a single v3 array.
    /// </summary>
    private static async Task<ZarrStoreMetadata> ReadV3MetadataAsync(
        ICloudRangeReader reader,
        string bucket,
        string normalizedRoot,
        JsonDocument rootDoc,
        CancellationToken cancellationToken)
    {
        var root = rootDoc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Zarr v3 zarr.json document is malformed.");
        }

        var nodeType = ValidateV3Node(root, expectedNodeType: null, normalizedRoot);

        var storeAttrs = root.TryGetProperty("attributes", out var attrsEl) && attrsEl.ValueKind == JsonValueKind.Object
            ? attrsEl
            : default;

        var arrays = new List<ZarrArrayMetadata>();
        if (string.Equals(nodeType, "array", StringComparison.Ordinal))
        {
            arrays.Add(ReadV3Array(root, ResolveSingleName(normalizedRoot), relativePath: string.Empty));
        }
        else
        {
            // Group node: resolve member arrays from the attributes `variables` manifest.
            var variables = ResolveV3Variables(storeAttrs);
            if (variables.Count == 0)
            {
                throw new InvalidDataException("Zarr v3 group discovery without an attributes.variables manifest is not supported.");
            }
            foreach (var name in variables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var arrayPath = JoinKey(normalizedRoot, name);
                var arrayDoc = await TryReadJsonDocumentAsync(reader, bucket, JoinKey(arrayPath, "zarr.json"), cancellationToken)
                    .ConfigureAwait(false);
                if (arrayDoc is null)
                {
                    throw new InvalidDataException($"Zarr v3 array '{name}' is missing a zarr.json at '{arrayPath}/zarr.json'.");
                }
                using (arrayDoc)
                {
                    arrays.Add(ReadV3Array(arrayDoc.RootElement, name, relativePath: name));
                }
            }
        }

        if (arrays.Count == 0)
        {
            throw new InvalidDataException("Zarr v3 store contains no arrays.");
        }

        var (srid, extent, primary, xDim, yDim, tDim, temporal, axes) =
            ResolveStoreGeoreferencing(storeAttrs.ValueKind == JsonValueKind.Object ? storeAttrs : (JsonElement?)null, arrays);

        return new ZarrStoreMetadata(
            ZarrFormat: ZarrFormatVersion.V3,
            Srid: srid,
            Extent: extent,
            Arrays: arrays.ToArray(),
            PrimaryVariable: primary,
            SpatialXDimension: xDim,
            SpatialYDimension: yDim,
            TemporalDimension: tDim,
            Temporal: temporal,
            Axes: axes);
    }

    private static string ValidateV3Node(JsonElement root, string? expectedNodeType, string name)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("zarr_format", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var format) || format != 3)
        {
            throw new InvalidDataException($"Zarr v3 node '{name}' must declare integer zarr_format 3.");
        }
        if (!root.TryGetProperty("node_type", out var type) || type.ValueKind != JsonValueKind.String ||
            type.GetString() is not ("group" or "array") ||
            expectedNodeType is not null && type.GetString() != expectedNodeType)
        {
            throw new InvalidDataException($"Zarr v3 node '{name}' has an invalid or unsupported node_type.");
        }
        var nodeType = type.GetString()!;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is "zarr_format" or "node_type") continue;
            if (property.Name == "attributes")
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"Zarr v3 node '{name}' attributes must be an object.");
                continue;
            }
            if (nodeType == "array" && property.Name is ("data_type" or "chunk_grid" or "chunk_key_encoding") &&
                property.Value.ValueKind == JsonValueKind.Object)
            {
                ValidateExtensionObject(property.Value, allowOptional: false, property.Name, name);
            }
            if (nodeType == "array" && property.Name == "codecs" && property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var codec in property.Value.EnumerateArray())
                {
                    if (codec.ValueKind == JsonValueKind.Object)
                        ValidateExtensionObject(codec, allowOptional: true, "codec", name);
                }
            }
            if (nodeType == "array" && property.Name is ("shape" or "data_type" or "chunk_grid" or "chunk_key_encoding" or "fill_value" or "codecs" or "dimension_names"))
                continue;
            if (nodeType == "array" && property.Name == "storage_transformers")
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException($"Zarr v3 node '{name}' storage_transformers must be an array.");
                foreach (var transformer in property.Value.EnumerateArray())
                    RequireIgnorableExtension(transformer, "storage transformer", name);
                continue;
            }
            if (nodeType == "group" && property.Name == "consolidated_metadata")
            {
                var value = property.Value;
                if (value.ValueKind != JsonValueKind.Object ||
                    !value.TryGetProperty("must_understand", out var flag) || flag.ValueKind != JsonValueKind.False ||
                    !value.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != "inline" ||
                    !value.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"Zarr v3 group '{name}' has malformed consolidated_metadata.");
                // This optional cache is ignored; the supported manifest resolves actual node documents.
                continue;
            }
            RequireIgnorableExtension(property.Value, property.Name, name);
        }
        return nodeType;
    }

    private static void RequireIgnorableExtension(JsonElement value, string field, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || ValidateExtensionObject(value, allowOptional: true, field, name))
            throw new InvalidDataException($"Zarr v3 node '{name}' declares unsupported required extension '{field}'.");
    }

    private static bool ValidateExtensionObject(JsonElement value, bool allowOptional, string field, string name)
    {
        if (!value.TryGetProperty("name", out var extensionName) || extensionName.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(extensionName.GetString()) ||
            value.TryGetProperty("configuration", out var configuration) && configuration.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Zarr v3 node '{name}' has malformed extension '{field}'.");
        if (!value.TryGetProperty("must_understand", out var flag)) return true;
        if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !allowOptional && flag.ValueKind == JsonValueKind.False)
            throw new InvalidDataException($"Zarr v3 node '{name}' has an invalid must_understand flag for '{field}'.");
        return flag.ValueKind != JsonValueKind.False;
    }

    private static string ReadExtensionName(JsonElement value, string field, string name)
    {
        if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            return value.GetString()!;
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("name", out var extensionName) &&
            extensionName.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(extensionName.GetString()))
            return extensionName.GetString()!;
        throw new InvalidDataException($"Zarr v3 array '{name}' has malformed extension '{field}'.");
    }

    private static List<string> ResolveV3Variables(JsonElement attributes)
    {
        var variables = new List<string>();
        if (attributes.ValueKind != JsonValueKind.Object ||
            !attributes.TryGetProperty("variables", out var variablesEl) ||
            variablesEl.ValueKind != JsonValueKind.Array)
        {
            return variables;
        }

        foreach (var entry in variablesEl.EnumerateArray())
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

        return variables;
    }

    private static ZarrArrayMetadata ReadV3Array(JsonElement root, string name, string relativePath)
    {
        ValidateV3Node(root, "array", name);
        foreach (var field in new[] { "shape", "data_type", "chunk_grid", "chunk_key_encoding", "fill_value", "codecs" })
        {
            if (!root.TryGetProperty(field, out _))
            {
                throw new InvalidDataException($"Zarr v3 array '{name}' is missing required property '{field}'.");
            }
        }

        var shape = ReadIntArray(root, "shape", name);
        if (shape.Length == 0)
        {
            throw new InvalidDataException($"Scalar Zarr v3 arrays such as '{name}' are not supported for coverage serving.");
        }
        if (shape.Any(dim => dim < 0))
        {
            throw new InvalidDataException($"Zarr v3 array '{name}' has a negative shape entry.");
        }
        if (shape.Contains(0))
        {
            throw new InvalidDataException($"Empty Zarr v3 dimensions in array '{name}' are not supported for coverage serving.");
        }

        var chunks = ReadV3ChunkShape(root, shape.Length, name);

        var dataType = NormalizeV3DataType(ReadRequiredString(root, "data_type", name), name);

        var compressor = ResolveV3Codecs(root, name, dataType);

        var fillValue = ZarrFillValueCodec.Parse(root.GetProperty("fill_value"), dataType, name, v3: true);

        var chunkKeyEncoding = ResolveV3ChunkKeyEncoding(root, name);
        var dimNames = ReadV3DimensionNames(root, shape.Length);

        return new ZarrArrayMetadata(
            Name: name,
            ZarrFormat: ZarrFormatVersion.V3,
            RelativePath: relativePath,
            Shape: shape,
            Chunks: chunks,
            DataType: dataType,
            Order: "C",
            Compressor: compressor,
            FillValue: fillValue,
            DimensionNames: dimNames,
            ChunkKeyEncoding: chunkKeyEncoding);
    }

    private static int[] ReadV3ChunkShape(JsonElement root, int rank, string name)
    {
        if (!root.TryGetProperty("chunk_grid", out var gridEl) || gridEl.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Zarr v3 array '{name}' is missing chunk_grid.");
        }

        var gridName = gridEl.TryGetProperty("name", out var gridNameEl) && gridNameEl.ValueKind == JsonValueKind.String
            ? gridNameEl.GetString()
            : null;
        if (!string.Equals(gridName, "regular", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Zarr v3 array '{name}' uses unsupported chunk_grid '{gridName}'; only 'regular' is supported.");
        }

        if (!gridEl.TryGetProperty("configuration", out var configEl) || configEl.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Zarr v3 array '{name}' chunk_grid is missing its configuration.");
        }

        var chunks = ReadIntArray(configEl, "chunk_shape", name);
        if (chunks.Length != rank)
        {
            throw new InvalidDataException($"Zarr v3 array '{name}' chunk_shape rank does not match shape rank.");
        }
        if (chunks.Any(chunk => chunk <= 0))
        {
            throw new InvalidDataException($"Zarr v3 array '{name}' has non-positive chunk entry.");
        }

        return chunks;
    }

    /// <summary>
    /// Resolves the chunk key encoding for a v3 array. Supports the v3 <c>default</c>
    /// encoding (<c>c</c>-prefixed, configurable separator) and <c>v2</c> encoding
    /// (dotted, no prefix). The encoding metadata is required; an omitted separator
    /// defaults to slash for <c>default</c> and dot for <c>v2</c>.
    /// </summary>
    private static ZarrChunkKeyEncoding ResolveV3ChunkKeyEncoding(JsonElement root, string name)
    {
        var encoding = root.GetProperty("chunk_key_encoding");
        var kind = ReadExtensionName(encoding, "chunk_key_encoding", name);
        if (kind is not ("default" or "v2"))
            throw new InvalidDataException($"Zarr v3 array '{name}' uses unsupported chunk_key_encoding '{kind}'.");
        var separator = kind == "default" ? "/" : ".";
        if (encoding.ValueKind == JsonValueKind.Object && encoding.TryGetProperty("configuration", out var configuration))
        {
            if (configuration.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Zarr v3 array '{name}' has malformed chunk-key configuration.");
            if (configuration.TryGetProperty("separator", out var value))
            {
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not ("/" or "."))
                    throw new InvalidDataException($"Zarr v3 array '{name}' has invalid chunk-key separator.");
                separator = value.GetString()!;
            }
        }
        return kind == "default"
            ? new ZarrChunkKeyEncoding(separator, "c", "c" + separator + "0")
            : new ZarrChunkKeyEncoding(separator, string.Empty, "0");
    }

    /// <summary>
    /// Inspects the v3 codec pipeline. Requires a leading <c>bytes</c> (endianness)
    /// codec and at most one trailing compression codec. Gzip uses its RFC 1952
    /// container, distinct from the RFC 1950 zlib container used by v2 chunks.
    /// Uncompressed arrays return a null compressor. Unsupported codecs fail explicitly.
    /// </summary>
    private static string? ResolveV3Codecs(JsonElement root, string name, string dataType)
    {
        if (!root.TryGetProperty("codecs", out var codecsEl) || codecsEl.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Zarr v3 array '{name}' is missing its codec pipeline.");
        }

        if (codecsEl.GetArrayLength() is < 1 or > 2)
        {
            throw new InvalidDataException($"Zarr v3 array '{name}' requires one bytes codec followed by at most one gzip codec.");
        }

        string? compressor = null;
        var codecIndex = 0;
        foreach (var codec in codecsEl.EnumerateArray())
        {
            var codecName = ReadExtensionName(codec, "codec", name);
            switch (codecName)
            {
                case "bytes":
                    if (codecIndex != 0)
                    {
                        throw new InvalidDataException($"Zarr v3 array '{name}' requires bytes as its first and only array-to-bytes codec.");
                    }
                    JsonElement bytesCfg = default;
                    var hasConfiguration = codec.ValueKind == JsonValueKind.Object && codec.TryGetProperty("configuration", out bytesCfg);
                    if (hasConfiguration && bytesCfg.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidDataException($"Zarr v3 array '{name}' has malformed bytes codec configuration.");
                    }
                    var hasEndian = hasConfiguration && bytesCfg.TryGetProperty("endian", out _);
                    if (!hasEndian && dataType[0] != '|')
                    {
                        throw new InvalidDataException($"Zarr v3 array '{name}' requires bytes codec endianness for multi-byte samples.");
                    }
                    if (hasEndian)
                    {
                        var endianEl = bytesCfg.GetProperty("endian");
                        if (endianEl.ValueKind != JsonValueKind.String || endianEl.GetString() is not ("little" or "big"))
                        {
                            throw new InvalidDataException($"Zarr v3 array '{name}' has invalid bytes codec endianness.");
                        }
                        if (endianEl.GetString() == "big" && dataType[0] != '|')
                        {
                            throw new InvalidDataException($"Zarr v3 array '{name}' declares big-endian bytes codec; only little-endian is supported.");
                        }
                    }
                    break;
                case "gzip":
                    if (codecIndex != 1)
                    {
                        throw new InvalidDataException($"Zarr v3 array '{name}' requires gzip to follow a bytes codec.");
                    }
                    if (codec.ValueKind != JsonValueKind.Object || !codec.TryGetProperty("configuration", out var gzipCfg) ||
                        gzipCfg.ValueKind != JsonValueKind.Object ||
                        !gzipCfg.TryGetProperty("level", out var levelEl) ||
                        levelEl.ValueKind != JsonValueKind.Number ||
                        !levelEl.TryGetInt32(out var level) || level is < 0 or > 9)
                    {
                        throw new InvalidDataException($"Zarr v3 array '{name}' requires gzip codec level to be an integer from 0 to 9.");
                    }
                    compressor = "gzip";
                    break;
                case "zstd":
                case "blosc":
                case "sharding_indexed":
                case "crc32c":
                    throw new InvalidDataException($"Zarr v3 array '{name}' uses codec '{codecName}', which is not supported by the reader. Use uncompressed or gzip-coded chunks.");
                default:
                    throw new InvalidDataException($"Zarr v3 array '{name}' uses unsupported codec '{codecName}'.");
            }
            codecIndex++;
        }

        return compressor;
    }

    /// <summary>
    /// Normalizes a Zarr v3 <c>data_type</c> name (e.g. <c>float32</c>) to the
    /// numpy little-endian dtype string the subset reader and CoverageJSON writer
    /// consume (e.g. <c>&lt;f4</c>). Rejects unsupported data types cleanly.
    /// </summary>
    internal static string NormalizeV3DataType(string dataType, string name) => dataType switch
    {
        "bool" => "|b1",
        "int8" => "|i1",
        "uint8" => "|u1",
        "int16" => "<i2",
        "uint16" => "<u2",
        "int32" => "<i4",
        "uint32" => "<u4",
        "int64" => "<i8",
        "uint64" => "<u8",
        "float32" => "<f4",
        "float64" => "<f8",
        _ => throw new InvalidDataException($"Zarr v3 array '{name}' uses unsupported data_type '{dataType}'.")
    };

    private static string[] ReadV3DimensionNames(JsonElement root, int rank)
    {
        if (root.TryGetProperty("dimension_names", out var dimensions))
        {
            if (dimensions.ValueKind != JsonValueKind.Array || dimensions.GetArrayLength() != rank ||
                dimensions.EnumerateArray().Any(value => value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                throw new InvalidDataException("Zarr v3 dimension_names must contain one string or null per array dimension.");
            return dimensions.EnumerateArray().Select((value, index) =>
                value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString())
                    ? value.GetString()!
                    : "dim_" + index.ToString(CultureInfo.InvariantCulture)).ToArray();
        }
        return Enumerable.Range(0, rank).Select(index => "dim_" + index.ToString(CultureInfo.InvariantCulture)).ToArray();
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

    private static string? ResolveCompressorId(JsonElement root, string arrayName)
    {
        var element = root.GetProperty("compressor");
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(id.GetString()))
            throw new InvalidDataException($"Zarr v2 array '{arrayName}' has malformed compressor metadata.");
        return id.GetString();
    }

    private static void RejectUnsupportedFilters(JsonElement root, string arrayName)
    {
        if (root.TryGetProperty("filters", out var filtersEl) &&
            filtersEl.ValueKind != JsonValueKind.Null &&
            !(filtersEl.ValueKind == JsonValueKind.Array && filtersEl.GetArrayLength() == 0))
        {
            throw new InvalidDataException($"Zarr array '{arrayName}' declares filters, which are not supported by the MVP reader.");
        }
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

    private static (int Srid, RasterExtent Extent, string? PrimaryVariable, string? XDim, string? YDim, string? TDim, TemporalExtent? Temporal, ZarrAxis[] Axes)
        ResolveStoreGeoreferencing(JsonElement? attrsElement, List<ZarrArrayMetadata> arrays)
    {
        var srid = 0;
        double xMin = 0, yMin = 0, xMax = 0, yMax = 0;
        bool hasExtent = false;
        string? primary = null;
        string? xDim = null;
        string? yDim = null;
        string? tDim = null;
        TemporalExtent? temporal = null;
        ZarrAxis[] axes = [];

        if (attrsElement is { ValueKind: JsonValueKind.Object } root)
        {
            if (root.TryGetProperty("crs_wkid", out var crsEl) && crsEl.ValueKind == JsonValueKind.Number && crsEl.TryGetInt32(out var crsValue))
            {
                srid = crsValue;
            }
            else if (root.TryGetProperty("crs", out var crsStringEl) && crsStringEl.ValueKind == JsonValueKind.String &&
                     TryParseEpsg(crsStringEl.GetString(), out var parsedSrid))
            {
                srid = parsedSrid;
            }

            if (root.TryGetProperty("extent", out var extentEl) && extentEl.ValueKind == JsonValueKind.Array)
            {
                var coords = new List<double>();
                coords.AddRange(extentEl.EnumerateArray()
                    .Select(entry =>
                        entry.ValueKind == JsonValueKind.Number &&
                        entry.TryGetDouble(out var value)
                            ? (double?)value
                            : null)
                    .Where(value => value.HasValue)
                    .Select(value => value.GetValueOrDefault()));
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
            temporal = ResolveTemporalExtent(root, tDim, arrays);
            axes = ResolveAdditionalAxes(root, xDim, yDim, tDim, arrays);
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

        return (srid, extent, primary, xDim, yDim, tDim, temporal, axes);
    }

    /// <summary>
    /// Builds the additional (non-spatial, non-temporal) coordinate axes from an
    /// optional <c>axes</c> manifest in the store <c>.zattrs</c>. Each entry is a
    /// <c>{ "name", "coordinates":[...] }</c> object for an explicit (possibly
    /// irregular) axis, or a <c>{ "name", "start", "end" }</c> object for an
    /// evenly-spaced axis; the sample count is taken from the matching array
    /// dimension. Spatial and temporal dimensions are excluded so they keep their
    /// dedicated handling. Entries that do not match any array dimension, or that
    /// declare neither coordinates nor a start/end pair, are skipped.
    /// </summary>
    private static ZarrAxis[] ResolveAdditionalAxes(
        JsonElement root,
        string? xDim,
        string? yDim,
        string? tDim,
        List<ZarrArrayMetadata> arrays)
    {
        if (!root.TryGetProperty("axes", out var axesEl) || axesEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<ZarrAxis>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in axesEl.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = ReadOptionalString(entry, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            // Spatial and temporal axes keep their dedicated handling.
            if (IsSameDimension(name, xDim) || IsSameDimension(name, yDim) || IsSameDimension(name, tDim))
            {
                continue;
            }

            var count = ResolveDimensionLength(name!, arrays);
            if (count <= 0 || !seen.Add(name!))
            {
                continue;
            }

            var unit = ReadOptionalString(entry, "unit");
            var positive = ReadOptionalString(entry, "positive");

            if (entry.TryGetProperty("coordinates", out var coordsEl) && coordsEl.ValueKind == JsonValueKind.Array)
            {
                var coords = ReadDoubleArray(coordsEl);
                if (coords.Length != count)
                {
                    continue;
                }
                result.Add(new ZarrAxis(name!, count, coords, coords[0], coords[^1], unit, positive));
                continue;
            }

            if (TryReadDouble(entry, "start", out var start) && TryReadDouble(entry, "end", out var end))
            {
                result.Add(new ZarrAxis(name!, count, Coordinates: null, start, end, unit, positive));
            }
        }

        return result.ToArray();
    }

    private static bool IsSameDimension(string name, string? dimension)
        => dimension is not null && string.Equals(name, dimension, StringComparison.OrdinalIgnoreCase);

    private static long ResolveDimensionLength(string dimension, List<ZarrArrayMetadata> arrays)
    {
        foreach (var array in arrays)
        {
            for (var i = 0; i < array.DimensionNames.Length; i++)
            {
                if (string.Equals(array.DimensionNames[i], dimension, StringComparison.OrdinalIgnoreCase))
                {
                    return array.Shape[i];
                }
            }
        }
        return 0;
    }

    private static double[] ReadDoubleArray(JsonElement element)
    {
        var values = new List<double>();
        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Number && entry.TryGetDouble(out var d))
            {
                values.Add(d);
            }
            else
            {
                return [];
            }
        }
        return values.ToArray();
    }

    private static bool TryReadDouble(JsonElement root, string property, out double value)
    {
        value = 0;
        return root.TryGetProperty(property, out var el) &&
               el.ValueKind == JsonValueKind.Number &&
               el.TryGetDouble(out value);
    }

    /// <summary>
    /// Builds the store temporal extent from optional <c>.zattrs</c> keys
    /// <c>t_start</c>, <c>t_end</c>, and a step expressed as either
    /// <c>t_step</c> (ISO-8601 duration / RFC3339-parseable timespan) or
    /// <c>t_step_seconds</c> (numeric seconds). Step count is taken from the
    /// time dimension's length on any array that declares it. Returns null when
    /// the required attributes are absent or unresolvable.
    /// </summary>
    private static TemporalExtent? ResolveTemporalExtent(JsonElement root, string? tDim, List<ZarrArrayMetadata> arrays)
    {
        if (string.IsNullOrEmpty(tDim))
        {
            return null;
        }

        if (!TryReadDateTimeOffset(root, "t_start", out var start) ||
            !TryReadDateTimeOffset(root, "t_end", out var end))
        {
            return null;
        }

        var stepCount = ResolveTemporalStepCount(tDim, arrays);
        if (stepCount <= 0)
        {
            return null;
        }

        // The step attributes are validated for consistency but the indexer
        // derives spacing from (start, end, stepCount); we only require that a
        // step source is present so irregular axes opt out explicitly.
        if (!TryReadStepSeconds(root, out _))
        {
            return null;
        }

        return new TemporalExtent(start, end, stepCount);
    }

    private static long ResolveTemporalStepCount(string tDim, List<ZarrArrayMetadata> arrays)
    {
        foreach (var array in arrays)
        {
            for (var i = 0; i < array.DimensionNames.Length; i++)
            {
                if (string.Equals(array.DimensionNames[i], tDim, StringComparison.OrdinalIgnoreCase))
                {
                    return array.Shape[i];
                }
            }
        }
        return 0;
    }

    private static bool TryReadDateTimeOffset(JsonElement root, string property, out DateTimeOffset value)
    {
        value = default;
        if (!root.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        return DateTimeOffset.TryParse(
            el.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);
    }

    private static bool TryReadStepSeconds(JsonElement root, out double seconds)
    {
        seconds = 0;
        if (root.TryGetProperty("t_step_seconds", out var secEl) &&
            secEl.ValueKind == JsonValueKind.Number &&
            secEl.TryGetDouble(out var parsedSeconds) &&
            parsedSeconds > 0)
        {
            seconds = parsedSeconds;
            return true;
        }

        if (root.TryGetProperty("t_step", out var stepEl))
        {
            if (stepEl.ValueKind == JsonValueKind.Number && stepEl.TryGetDouble(out var numericStep) && numericStep > 0)
            {
                seconds = numericStep;
                return true;
            }
            if (stepEl.ValueKind == JsonValueKind.String &&
                TimeSpan.TryParse(stepEl.GetString(), CultureInfo.InvariantCulture, out var span) &&
                span.Ticks > 0)
            {
                seconds = span.TotalSeconds;
                return true;
            }
        }

        return false;
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
        long objectSize;
        try
        {
            objectSize = await reader.GetObjectSizeAsync(bucket, key, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        if (objectSize <= 0)
        {
            return null;
        }
        if (objectSize > MaxMetadataBytes)
        {
            throw new InvalidDataException(
                $"Zarr metadata at '{key}' is {objectSize:N0} bytes, exceeding the {MaxMetadataBytes:N0} byte safety limit.");
        }

        var payload = await reader.ReadRangeAsync(
                bucket, key, 0, checked((int)objectSize), cancellationToken)
            .ConfigureAwait(false);

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
