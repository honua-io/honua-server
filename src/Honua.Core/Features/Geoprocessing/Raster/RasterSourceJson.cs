// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>Source-generated JSON contract for durable raster source descriptors.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RasterSourceDescriptor))]
[JsonSerializable(typeof(PostgisRasterSourceDescriptor))]
[JsonSerializable(typeof(ObjectStoreCogRasterSourceDescriptor))]
[JsonSerializable(typeof(ObjectStoreZarrRasterSourceDescriptor))]
[JsonSerializable(typeof(StagedArtifactRasterSourceDescriptor))]
[JsonSerializable(typeof(InlineRasterSourceDescriptor))]
[JsonSerializable(typeof(RasterContentIdentity))]
[JsonSerializable(typeof(RasterChecksum))]
[JsonSerializable(typeof(RasterSecurityContextReference))]
[JsonSerializable(typeof(RasterSourceSelection))]
[JsonSerializable(typeof(RasterPixelWindow))]
[JsonSerializable(typeof(RasterTimeSelection))]
[JsonSerializable(typeof(RasterDimensionSlice))]
[JsonSerializable(typeof(Dictionary<string, RasterSourceDescriptor>))]
public sealed partial class RasterSourceJsonContext : JsonSerializerContext
{
}

/// <summary>AOT-safe serialization helpers for the versioned raster source contract.</summary>
public static class RasterSourceJson
{
    /// <summary>Serializes a descriptor through source-generated metadata.</summary>
    /// <param name="descriptor">Descriptor to serialize.</param>
    /// <returns>Canonical JSON for durable transport.</returns>
    public static string Serialize(RasterSourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return JsonSerializer.Serialize(descriptor, RasterSourceJsonContext.Default.RasterSourceDescriptor);
    }

    /// <summary>Deserializes a descriptor through source-generated metadata.</summary>
    /// <param name="json">Descriptor JSON.</param>
    /// <returns>The typed descriptor.</returns>
    /// <exception cref="JsonException">The payload is malformed or has an unknown source type.</exception>
    public static RasterSourceDescriptor Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(json, RasterSourceJsonContext.Default.RasterSourceDescriptor)
            ?? throw new JsonException("Raster source descriptor cannot be null.");
    }
}
