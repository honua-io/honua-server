// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Style payload carried on a <see cref="MetadataV2Resource"/> whose
/// <see cref="MetadataV2Resource.Type"/> is <see cref="MetadataV2ResourceType.Style"/>.
/// One style resource may carry the same logical style in multiple encodings
/// (MapLibre + SLD + Esri drawing info) — each encoding entry is a separate
/// element in <see cref="Encodings"/>.
/// </summary>
public sealed record MetadataV2ResourceStyle
{
    private IReadOnlyList<MetadataV2StyleEncoding>? _encodings;

    /// <summary>Human-readable style title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Free-form style abstract / description.</summary>
    [JsonPropertyName("abstract")]
    public string? Abstract { get; init; }

    /// <summary>Optional legend graphic URL.</summary>
    [JsonPropertyName("legendUrl")]
    public string? LegendUrl { get; init; }

    /// <summary>Author-managed integer version of this style document.</summary>
    [JsonPropertyName("styleVersion")]
    public int StyleVersion { get; init; }

    /// <summary>Encoded representations of this style. Must contain at least one entry.</summary>
    [JsonPropertyName("encodings")]
    public IReadOnlyList<MetadataV2StyleEncoding> Encodings
    {
        get => _encodings ?? Array.Empty<MetadataV2StyleEncoding>();
        init => _encodings = value;
    }
}

/// <summary>
/// Single encoded representation of a <see cref="MetadataV2ResourceStyle"/>.
/// Exactly one of <see cref="Body"/> or <see cref="StorageBindingId"/> must be
/// set — the encoded style is either inlined into the graph or stored
/// externally and referenced by a storage binding.
/// </summary>
public sealed record MetadataV2StyleEncoding
{
    /// <summary>
    /// Style encoding identifier. Canonical values: <c>mapbox-style</c>,
    /// <c>sld-1.0.0</c>, <c>sld-1.1.0</c>, <c>esri-drawing-info</c>,
    /// <c>esri-image-renderer</c>, <c>3d-tiles-styling</c>. Custom encodings
    /// use the <c>x-</c> prefix (e.g. <c>x-my-renderer</c>).
    /// </summary>
    [JsonPropertyName("encoding")]
    public string Encoding { get; init; } = string.Empty;

    /// <summary>
    /// Inline serialized style body (JSON / XML / GLSL / …). Mutually
    /// exclusive with <see cref="StorageBindingId"/>.
    /// </summary>
    [JsonPropertyName("body")]
    public string? Body { get; init; }

    /// <summary>
    /// Storage binding that materializes this encoded style. Mutually
    /// exclusive with <see cref="Body"/>.
    /// </summary>
    [JsonPropertyName("storageBindingId")]
    public string? StorageBindingId { get; init; }

    /// <summary>Optional content-type override for the encoded body.</summary>
    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }
}
