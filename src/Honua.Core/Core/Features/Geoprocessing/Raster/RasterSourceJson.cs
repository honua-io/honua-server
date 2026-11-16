// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Geoprocessing.Raster;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true,
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
[JsonSerializable(typeof(RasterSourceDimensions))]
[JsonSerializable(typeof(RasterSourcePixelScale))]
[JsonSerializable(typeof(RasterPixelWindow))]
[JsonSerializable(typeof(RasterTimeSelection))]
[JsonSerializable(typeof(RasterDimensionSlice))]
[JsonSerializable(typeof(Dictionary<string, RasterSourceDescriptor>))]
public sealed partial class RasterSourceJsonContext : JsonSerializerContext
{
}

public static class RasterSourceJson
{
    public static string Serialize(RasterSourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return JsonSerializer.Serialize(descriptor, RasterSourceJsonContext.Default.RasterSourceDescriptor);
    }

    public static RasterSourceDescriptor Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(json, RasterSourceJsonContext.Default.RasterSourceDescriptor)
            ?? throw new JsonException("Raster source descriptor cannot be null.");
    }
}
