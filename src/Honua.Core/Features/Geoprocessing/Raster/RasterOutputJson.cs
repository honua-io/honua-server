// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>Source-generated JSON contract for durable raster outputs and staging manifests.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RasterOutputDescriptor))]
[JsonSerializable(typeof(ObjectStoreRasterOutputDescriptor))]
[JsonSerializable(typeof(PostgisRasterOutputDescriptor))]
[JsonSerializable(typeof(InlineRasterOutputDescriptor))]
[JsonSerializable(typeof(StagedRasterOutputDescriptor))]
[JsonSerializable(typeof(RasterOutputPublicationManifest))]
[JsonSerializable(typeof(StagedRasterOutputDescriptor[]))]
[JsonSerializable(typeof(RasterGridMetadata))]
[JsonSerializable(typeof(RasterProducingEngine))]
[JsonSerializable(typeof(RasterOutputLineage))]
[JsonSerializable(typeof(RasterOutputRetention))]
[JsonSerializable(typeof(RasterOutputPublicationRequest))]
[JsonSerializable(typeof(RasterOutputRegistrationTarget))]
[JsonSerializable(typeof(RasterOutputRegistrationCommand))]
[JsonSerializable(typeof(RasterOutputRegistrationResult))]
public sealed partial class RasterOutputJsonContext : JsonSerializerContext
{
}

/// <summary>AOT-safe serialization helpers for versioned raster output descriptors.</summary>
public static class RasterOutputJson
{
    /// <summary>Serializes a published descriptor with source-generated metadata.</summary>
    public static string Serialize(RasterOutputDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return JsonSerializer.Serialize(descriptor, RasterOutputJsonContext.Default.RasterOutputDescriptor);
    }

    /// <summary>Deserializes a published descriptor with source-generated metadata.</summary>
    public static RasterOutputDescriptor Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(json, RasterOutputJsonContext.Default.RasterOutputDescriptor)
            ?? throw new JsonException("Raster output descriptor cannot be null.");
    }

    /// <summary>Serializes a metadata-only staged output manifest entry.</summary>
    public static string SerializeStage(StagedRasterOutputDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return JsonSerializer.Serialize(descriptor, RasterOutputJsonContext.Default.StagedRasterOutputDescriptor);
    }

    /// <summary>Deserializes a metadata-only staged output manifest entry.</summary>
    public static StagedRasterOutputDescriptor DeserializeStage(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(json, RasterOutputJsonContext.Default.StagedRasterOutputDescriptor)
            ?? throw new JsonException("Staged raster output descriptor cannot be null.");
    }

    /// <summary>Serializes one metadata-only attempt publication manifest.</summary>
    public static string SerializeManifest(RasterOutputPublicationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(
            manifest,
            RasterOutputJsonContext.Default.RasterOutputPublicationManifest);
    }

    /// <summary>Deserializes one metadata-only attempt publication manifest.</summary>
    public static RasterOutputPublicationManifest DeserializeManifest(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(
            json,
            RasterOutputJsonContext.Default.RasterOutputPublicationManifest)
            ?? throw new JsonException("Raster output publication manifest cannot be null.");
    }
}
