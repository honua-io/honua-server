// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

internal static class ZarrConsolidatedMetadataReader
{
    private static readonly string[] RequiredV2ArrayFields =
        ["zarr_format", "shape", "chunks", "dtype", "compressor", "fill_value", "order", "filters"];

    public static JsonElement? TryGetV2ConsolidatedMap(JsonDocument? consolidatedDoc)
        => consolidatedDoc is not null
           && consolidatedDoc.RootElement.ValueKind == JsonValueKind.Object
           && consolidatedDoc.RootElement.TryGetProperty("metadata", out var map)
           && map.ValueKind == JsonValueKind.Object
            ? map
            : null;

    public static JsonElement? ResolveV2RootElement(JsonDocument? directDoc, JsonElement? consolidatedMap, string key)
    {
        if (directDoc is not null)
        {
            return directDoc.RootElement;
        }

        if (consolidatedMap is { ValueKind: JsonValueKind.Object } map
            && map.TryGetProperty(key, out var entry)
            && entry.ValueKind == JsonValueKind.Object)
        {
            return entry;
        }

        return null;
    }

    public static async Task<List<ZarrArrayMetadata>> ReadV2ArraysAsync(
        V2ArrayReadContext context,
        List<string> variables,
        CancellationToken cancellationToken)
    {
        var arrays = new List<ZarrArrayMetadata>();
        if (variables.Count == 0)
        {
            var singleTarget = new V2ArrayTarget
            {
                RelativePath = string.Empty,
                Name = ZarrMetadataExtractor.ResolveSingleName(context.NormalizedRoot),
            };
            arrays.Add(await ReadV2ArrayWithConsolidationAsync(context, singleTarget, cancellationToken).ConfigureAwait(false));
            return arrays;
        }

        foreach (var name in variables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = new V2ArrayTarget { RelativePath = name, Name = name };
            arrays.Add(await ReadV2ArrayWithConsolidationAsync(context, target, cancellationToken).ConfigureAwait(false));
        }

        return arrays;
    }

    public static JsonElement? TryGetV3ConsolidatedMap(JsonElement root)
        => root.TryGetProperty("consolidated_metadata", out var consolidated)
           && consolidated.ValueKind == JsonValueKind.Object
           && consolidated.TryGetProperty("metadata", out var metadata)
           && metadata.ValueKind == JsonValueKind.Object
            ? metadata
            : null;

    public static void AddV3ConsolidatedVariables(JsonElement consolidatedMap, List<string> variables, int maxVariables)
    {
        foreach (var property in consolidatedMap.EnumerateObject())
        {
            if (!IsTopLevelV3ArrayEntry(property))
            {
                continue;
            }

            if (variables.Count >= maxVariables)
            {
                throw new InvalidDataException($"Zarr store exposes more than {maxVariables} variables; reduce the manifest or split the store.");
            }

            variables.Add(property.Name);
        }
    }

    public static async Task<List<ZarrArrayMetadata>> ReadV3GroupArraysAsync(
        V3GroupReadContext context,
        List<string> variables,
        CancellationToken cancellationToken)
    {
        var arrays = new List<ZarrArrayMetadata>(variables.Count);
        foreach (var name in variables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            arrays.Add(await ReadV3GroupMemberAsync(context, name, cancellationToken).ConfigureAwait(false));
        }

        return arrays;
    }

    private static async Task<ZarrArrayMetadata> ReadV3GroupMemberAsync(
        V3GroupReadContext context,
        string name,
        CancellationToken cancellationToken)
    {
        if (context.ConsolidatedMap is { ValueKind: JsonValueKind.Object } map
            && map.TryGetProperty(name, out var consolidatedArray)
            && consolidatedArray.ValueKind == JsonValueKind.Object)
        {
            return context.ParseArray(consolidatedArray, name, name);
        }

        var arrayPath = ZarrMetadataExtractor.JoinKey(context.NormalizedRoot, name);
        var arrayDoc = await ZarrMetadataExtractor.TryReadJsonDocumentAsync(
                context.Reader, context.Bucket, ZarrMetadataExtractor.JoinKey(arrayPath, "zarr.json"), cancellationToken)
            .ConfigureAwait(false);
        if (arrayDoc is null)
        {
            throw new InvalidDataException($"Zarr v3 array '{name}' is missing a zarr.json at '{arrayPath}/zarr.json'.");
        }

        using (arrayDoc)
        {
            return context.ParseArray(arrayDoc.RootElement, name, name);
        }
    }

    private static bool IsTopLevelV3ArrayEntry(JsonProperty property)
    {
        var name = property.Name;
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || ZarrMetadataExtractor.ContainsUnsafeSegment(name))
        {
            return false;
        }

        return property.Value.ValueKind == JsonValueKind.Object
            && property.Value.TryGetProperty("node_type", out var nodeType)
            && nodeType.ValueKind == JsonValueKind.String
            && string.Equals(nodeType.GetString(), "array", StringComparison.Ordinal);
    }

    private static Task<ZarrArrayMetadata> ReadV2ArrayWithConsolidationAsync(
        V2ArrayReadContext context,
        V2ArrayTarget target,
        CancellationToken cancellationToken)
    {
        var zarrayKey = string.IsNullOrEmpty(target.RelativePath) ? ".zarray" : target.RelativePath + "/.zarray";
        var zattrsKey = string.IsNullOrEmpty(target.RelativePath) ? ".zattrs" : target.RelativePath + "/.zattrs";
        if (context.ConsolidatedMap is { ValueKind: JsonValueKind.Object } map
            && map.TryGetProperty(zarrayKey, out var zarrayEl)
            && zarrayEl.ValueKind == JsonValueKind.Object
            && zarrayEl.TryGetProperty("zarr_format", out _))
        {
            JsonElement? zattrsEl = map.TryGetProperty(zattrsKey, out var foundAttrs)
                && foundAttrs.ValueKind == JsonValueKind.Object
                    ? foundAttrs
                    : null;
            return Task.FromResult(ParseV2Array(zarrayEl, zattrsEl, target));
        }

        return ReadV2ArrayFromStorageAsync(context, target, cancellationToken);
    }

    private static async Task<ZarrArrayMetadata> ReadV2ArrayFromStorageAsync(
        V2ArrayReadContext context,
        V2ArrayTarget target,
        CancellationToken cancellationToken)
    {
        var arrayPath = string.IsNullOrEmpty(target.RelativePath)
            ? context.NormalizedRoot
            : ZarrMetadataExtractor.JoinKey(context.NormalizedRoot, target.RelativePath);
        var zarrayDoc = await ZarrMetadataExtractor.TryReadJsonDocumentAsync(
                context.Reader, context.Bucket, ZarrMetadataExtractor.JoinKey(arrayPath, ".zarray"), cancellationToken)
            .ConfigureAwait(false);
        if (zarrayDoc is null)
        {
            throw new InvalidDataException($"Zarr array '{target.Name}' is missing a .zarray document at '{arrayPath}/.zarray'.");
        }

        var arrayAttrs = await ZarrMetadataExtractor.TryReadJsonDocumentAsync(
                context.Reader, context.Bucket, ZarrMetadataExtractor.JoinKey(arrayPath, ".zattrs"), cancellationToken)
            .ConfigureAwait(false);

        using (zarrayDoc)
        using (arrayAttrs)
        {
            return ParseV2Array(zarrayDoc.RootElement, arrayAttrs?.RootElement, target);
        }
    }

    private static ZarrArrayMetadata ParseV2Array(JsonElement root, JsonElement? arrayAttrs, V2ArrayTarget target)
    {
        ValidateV2ArrayRoot(root, target.Name);
        var format = ResolveValidatedV2Format(root, target.Name);
        var shape = ZarrMetadataExtractor.ReadIntArray(root, "shape", target.Name);
        var chunks = ZarrMetadataExtractor.ReadIntArray(root, "chunks", target.Name);
        ValidateV2ShapeAndChunks(shape, chunks, target.Name);

        var dtype = ZarrMetadataExtractor.ReadRequiredString(root, "dtype", target.Name);
        var order = ReadValidatedV2Order(root, target.Name);
        ZarrMetadataExtractor.RejectUnsupportedFilters(root, target.Name);

        var compressor = ZarrMetadataExtractor.ResolveCompressorId(root, target.Name);
        var fillValue = ZarrFillValueCodec.Parse(root.GetProperty("fill_value"), dtype, target.Name, v3: false);
        var dimensionSeparator = ResolveV2DimensionSeparator(root, target.Name);
        var dimNames = ResolveV2DimensionNames(root, arrayAttrs, shape.Length);

        return new ZarrArrayMetadata(
            Name: target.Name,
            ZarrFormat: format,
            RelativePath: target.RelativePath,
            Shape: shape,
            Chunks: chunks,
            DataType: dtype,
            Order: order,
            Compressor: compressor,
            FillValue: fillValue,
            DimensionNames: dimNames,
            ChunkKeyEncoding: new ZarrChunkKeyEncoding(dimensionSeparator, string.Empty, "0"));
    }

    private static void ValidateV2ArrayRoot(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Zarr array '{name}' has a malformed .zarray document.");
        }

        foreach (var field in RequiredV2ArrayFields)
        {
            if (!root.TryGetProperty(field, out _))
            {
                throw new InvalidDataException($"Zarr v2 array '{name}' is missing required property '{field}'.");
            }
        }
    }

    private static ZarrFormatVersion ResolveValidatedV2Format(JsonElement root, string name)
    {
        var format = ZarrMetadataExtractor.ResolveZarrFormat(root, name);
        if (format != ZarrFormatVersion.V2)
        {
            throw new InvalidDataException($"Zarr v{(int)format} is not supported by the MVP reader; use a Zarr v2 store.");
        }

        return format;
    }

    private static string ReadValidatedV2Order(JsonElement root, string name)
    {
        var order = ZarrMetadataExtractor.ReadRequiredString(root, "order", name);
        if (order is not ("C" or "F"))
        {
            throw new InvalidDataException($"Zarr array '{name}' has invalid chunk order '{order}'.");
        }

        if (!string.Equals(order, "C", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Zarr array '{name}' uses Fortran chunk order; only C order is supported by the MVP reader.");
        }

        return order;
    }

    private static void ValidateV2ShapeAndChunks(int[] shape, int[] chunks, string name)
    {
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
    }

    private static string ResolveV2DimensionSeparator(JsonElement root, string name)
    {
        if (!root.TryGetProperty("dimension_separator", out var separatorEl))
        {
            return ".";
        }

        if (separatorEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Zarr array '{name}' has a malformed dimension_separator.");
        }

        var separator = separatorEl.GetString() ?? string.Empty;
        if (separator is not ("." or "/"))
        {
            throw new InvalidDataException($"Zarr array '{name}' uses unsupported dimension_separator '{separator}'.");
        }

        return separator;
    }

    private static string[] ResolveV2DimensionNames(JsonElement root, JsonElement? arrayAttrs, int rank)
    {
        var dimNames = ZarrMetadataExtractor.ReadDimensionNames(root, rank);
        if (arrayAttrs is { } attrs && ZarrMetadataExtractor.ReadDimensionNamesFromAttrs(attrs, rank) is { } attrDims)
        {
            return attrDims;
        }

        return dimNames;
    }

    internal sealed class V2ArrayReadContext
    {
        public required ICloudRangeReader Reader { get; init; }

        public required string Bucket { get; init; }

        public required string NormalizedRoot { get; init; }

        public JsonElement? ConsolidatedMap { get; init; }
    }

    internal sealed class V3GroupReadContext
    {
        public required ICloudRangeReader Reader { get; init; }

        public required string Bucket { get; init; }

        public required string NormalizedRoot { get; init; }

        public JsonElement? ConsolidatedMap { get; init; }

        public required Func<JsonElement, string, string, ZarrArrayMetadata> ParseArray { get; init; }
    }

    private readonly record struct V2ArrayTarget
    {
        public required string RelativePath { get; init; }

        public required string Name { get; init; }
    }
}
