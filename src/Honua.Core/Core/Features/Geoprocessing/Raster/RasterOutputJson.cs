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
[JsonSerializable(typeof(RasterOutputDescriptor))]
[JsonSerializable(typeof(StagedObjectRasterOutputDescriptor))]
[JsonSerializable(typeof(PostgisRasterOutputDescriptor))]
[JsonSerializable(typeof(InlineRasterOutputDescriptor))]
[JsonSerializable(typeof(RasterContentIdentity))]
[JsonSerializable(typeof(RasterChecksum))]
[JsonSerializable(typeof(RasterOutputGridSummary))]
[JsonSerializable(typeof(RasterOutputLineage))]
[JsonSerializable(typeof(RasterSourcePixelScale))]
public sealed partial class RasterOutputJsonContext : JsonSerializerContext
{
}

public static class RasterOutputJson
{
    // Publication writes the discriminator first (source-generated polymorphic
    // serialization), so a cheap prefix probe avoids attempting a full parse on
    // every legacy data:/https: artifact reference.
    private const string EnvelopeProbe = "\"outputType\"";

    public static string Serialize(RasterOutputDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return JsonSerializer.Serialize(descriptor, RasterOutputJsonContext.Default.RasterOutputDescriptor);
    }

    public static RasterOutputDescriptor Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(json, RasterOutputJsonContext.Default.RasterOutputDescriptor)
            ?? throw new JsonException("Raster output descriptor cannot be null.");
    }

    public static bool LooksLikeDescriptor(string? artifactReference)
    {
        if (string.IsNullOrWhiteSpace(artifactReference))
        {
            return false;
        }

        var trimmed = artifactReference.AsSpan().TrimStart();
        return trimmed.Length > 0
            && trimmed[0] == '{'
            && trimmed.Contains(EnvelopeProbe, StringComparison.Ordinal);
    }

    public static bool TryDeserialize(
        string? artifactReference,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RasterOutputDescriptor? descriptor)
    {
        descriptor = null;
        if (string.IsNullOrWhiteSpace(artifactReference))
        {
            return false;
        }

        if (!LooksLikeDescriptor(artifactReference))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(
                artifactReference, RasterOutputJsonContext.Default.RasterOutputDescriptor);
            if (parsed is null ||
                parsed.OutputContractVersion < RasterOutputContract.MinimumSupportedVersion ||
                parsed.OutputContractVersion > RasterOutputContract.CurrentVersion)
            {
                return false;
            }

            descriptor = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
