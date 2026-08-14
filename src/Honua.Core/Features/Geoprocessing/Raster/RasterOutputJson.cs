// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>Source-generated JSON contract for durable raster output descriptors.</summary>
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

/// <summary>AOT-safe serialization helpers for the versioned raster output contract.</summary>
public static class RasterOutputJson
{
    // Publication writes the discriminator first (source-generated polymorphic
    // serialization), so a cheap prefix probe avoids attempting a full parse on
    // every legacy data:/https: artifact reference.
    private const string EnvelopeProbe = "\"outputType\"";

    /// <summary>Serializes a descriptor through source-generated metadata.</summary>
    /// <param name="descriptor">Descriptor to serialize.</param>
    /// <returns>Canonical JSON for durable transport.</returns>
    public static string Serialize(RasterOutputDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return JsonSerializer.Serialize(descriptor, RasterOutputJsonContext.Default.RasterOutputDescriptor);
    }

    /// <summary>Deserializes a descriptor through source-generated metadata.</summary>
    /// <param name="json">Descriptor JSON.</param>
    /// <returns>The typed descriptor.</returns>
    /// <exception cref="JsonException">The payload is malformed or has an unknown output type.</exception>
    public static RasterOutputDescriptor Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(json, RasterOutputJsonContext.Default.RasterOutputDescriptor)
            ?? throw new JsonException("Raster output descriptor cannot be null.");
    }

    /// <summary>
    /// Whether a durable artifact reference is descriptor-shaped (a JSON object
    /// carrying the <c>outputType</c> discriminator), regardless of whether this
    /// release can deserialize it. Readers must never expose a descriptor-shaped
    /// reference that fails <see cref="TryDeserialize"/> as a raw value: it carries
    /// store-internal identities (store reference, object key) that are not
    /// client-facing.
    /// </summary>
    /// <param name="artifactReference">Durable artifact reference string.</param>
    /// <returns>Whether the reference looks like a raster output descriptor.</returns>
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

    /// <summary>
    /// Attempts to interpret a durable artifact reference as a typed raster output
    /// descriptor. Returns <see langword="false"/> for legacy references (data URIs,
    /// provider links, workspace paths) and malformed or unsupported-version JSON.
    /// </summary>
    /// <param name="artifactReference">Durable artifact reference string.</param>
    /// <param name="descriptor">The parsed descriptor when recognized.</param>
    /// <returns>Whether the reference is a supported typed output descriptor.</returns>
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
