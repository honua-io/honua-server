// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

// ---- Display hints (MetadataV2ResourceDisplay) -----------------------------------------------------------

/// <summary>
/// Request payload for updating a layer's display / rendering hints
/// (<c>MetadataV2ResourceDisplay</c>). Null fields leave the corresponding stored
/// value unchanged.
/// </summary>
public sealed class LayerDisplayUpdateRequest
{
    /// <summary>Minimum scale denominator at which the layer is drawn. Null leaves unchanged.</summary>
    public double? MinScale { get; init; }

    /// <summary>Maximum scale denominator at which the layer is drawn. Null leaves unchanged.</summary>
    public double? MaxScale { get; init; }

    /// <summary>Whether the layer is visible by default. Null leaves unchanged.</summary>
    public bool? DefaultVisibility { get; init; }

    /// <summary>Display / label field name. Must reference a declared schema field. Null leaves unchanged.</summary>
    public string? DisplayField { get; init; }

    /// <summary>Whether the layer supports attribute queries. Null leaves unchanged.</summary>
    public bool? Queryable { get; init; }

    /// <summary>Whether geometries carry Z values. Null leaves unchanged.</summary>
    public bool? HasZ { get; init; }

    /// <summary>Whether geometries carry M values. Null leaves unchanged.</summary>
    public bool? HasM { get; init; }
}

/// <summary>Response payload echoing a layer's persisted display hints.</summary>
public sealed class LayerDisplayResponse
{
    public int LayerId { get; init; }

    public double? MinScale { get; init; }

    public double? MaxScale { get; init; }

    public bool DefaultVisibility { get; init; }

    public string? DisplayField { get; init; }

    public bool Queryable { get; init; }

    public bool HasZ { get; init; }

    public bool HasM { get; init; }
}

// ---- Editor tracking / edit capability (MetadataV2ResourceEditing) ---------------------------------------

/// <summary>
/// Request payload for updating a layer's editor-tracking / edit-capability hints
/// (<c>MetadataV2ResourceEditing</c>). Null fields leave the corresponding stored
/// value unchanged.
/// </summary>
public sealed class LayerEditingUpdateRequest
{
    /// <summary>Global-id field name. Must reference a declared schema field when set. Null leaves unchanged.</summary>
    public string? GlobalIdField { get; init; }

    /// <summary>Creating-user field name. Null leaves unchanged.</summary>
    public string? CreatorField { get; init; }

    /// <summary>Creation-timestamp field name. Null leaves unchanged.</summary>
    public string? CreatedAtField { get; init; }

    /// <summary>Last-editing-user field name. Null leaves unchanged.</summary>
    public string? EditorField { get; init; }

    /// <summary>Last-update-timestamp field name. Null leaves unchanged.</summary>
    public string? UpdatedAtField { get; init; }

    /// <summary>Whether records may be modified. Null leaves unchanged.</summary>
    public bool? CanModify { get; init; }

    /// <summary>Whether the layer supports attachments. Null leaves unchanged.</summary>
    public bool? SupportsAttachments { get; init; }

    /// <summary>Whether the layer supports related-record edits. Null leaves unchanged.</summary>
    public bool? SupportsRelatedRecords { get; init; }
}

/// <summary>Response payload echoing a layer's persisted editor-tracking hints.</summary>
public sealed class LayerEditingResponse
{
    public int LayerId { get; init; }

    public string? GlobalIdField { get; init; }

    public string? CreatorField { get; init; }

    public string? CreatedAtField { get; init; }

    public string? EditorField { get; init; }

    public string? UpdatedAtField { get; init; }

    public bool CanModify { get; init; }

    public bool SupportsAttachments { get; init; }

    public bool SupportsRelatedRecords { get; init; }
}

// ---- Discovery / catalog metadata (MetadataV2ObjectMetadata) ---------------------------------------------

/// <summary>
/// Contact-point payload for discovery metadata.
/// </summary>
public sealed class DiscoveryContactPoint
{
    public string? Name { get; init; }

    public string? Email { get; init; }

    public string? Url { get; init; }
}

/// <summary>
/// External link payload for discovery metadata (RFC 8288 / OGC-API / STAC link object).
/// </summary>
public sealed class DiscoveryLink
{
    public required string Href { get; init; }

    public required string Rel { get; init; }

    public string? Type { get; init; }

    public string? Title { get; init; }

    public string? Hreflang { get; init; }
}

/// <summary>
/// Request payload for updating discovery / catalog metadata
/// (<c>MetadataV2ObjectMetadata</c> discovery fields). Null fields leave the
/// corresponding stored value unchanged; an empty array clears a list field.
/// </summary>
public sealed class DiscoveryMetadataUpdateRequest
{
    /// <summary>Human-readable display title. Null leaves unchanged; empty string clears.</summary>
    public string? Title { get; init; }

    /// <summary>Human-readable description. Null leaves unchanged; empty string clears.</summary>
    public string? Description { get; init; }

    /// <summary>Free-form discovery keywords. Null leaves unchanged; empty array clears.</summary>
    public IReadOnlyList<string>? Keywords { get; init; }

    /// <summary>DCAT-style themes / categories. Null leaves unchanged; empty array clears.</summary>
    public IReadOnlyList<string>? Themes { get; init; }

    /// <summary>BCP-47 language tag. Null leaves unchanged; empty string clears.</summary>
    public string? Language { get; init; }

    /// <summary>SPDX license identifier. Null leaves unchanged; empty string clears.</summary>
    public string? License { get; init; }

    /// <summary>Attribution / credits string. Null leaves unchanged; empty string clears.</summary>
    public string? Attribution { get; init; }

    /// <summary>Data producer / publisher. Null leaves unchanged; empty string clears.</summary>
    public string? Publisher { get; init; }

    /// <summary>Point of contact. Null leaves unchanged.</summary>
    public DiscoveryContactPoint? ContactPoint { get; init; }

    /// <summary>External links. Null leaves unchanged; empty array clears.</summary>
    public IReadOnlyList<DiscoveryLink>? Links { get; init; }
}

/// <summary>Response payload echoing persisted discovery / catalog metadata.</summary>
public sealed class DiscoveryMetadataResponse
{
    /// <summary>Layer id when this describes a layer; null for a service-level response.</summary>
    public int? LayerId { get; init; }

    /// <summary>Service name when this describes a service; null for a layer-level response.</summary>
    public string? ServiceName { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Themes { get; init; } = Array.Empty<string>();

    public string? Language { get; init; }

    public string? License { get; init; }

    public string? Attribution { get; init; }

    public string? Publisher { get; init; }

    public DiscoveryContactPoint? ContactPoint { get; init; }

    public IReadOnlyList<DiscoveryLink> Links { get; init; } = Array.Empty<DiscoveryLink>();
}

// ---- CRS / spatial authoring (MetadataV2ResourceSpatial CRS-list / output-CRS fields) --------------------

/// <summary>
/// Spatial-reference payload for the CRS authoring endpoints.
/// </summary>
public sealed class SpatialReferencePayload
{
    /// <summary>EPSG numeric identifier when known.</summary>
    public int? Srid { get; init; }

    /// <summary>CRS URN or short identifier (e.g. <c>EPSG:4326</c>).</summary>
    public string? Crs { get; init; }

    /// <summary>Whether the CRS uses geographic (spherical) coordinates.</summary>
    public bool IsGeographic { get; init; }
}

/// <summary>
/// Request payload for updating a layer's CRS / spatial authoring fields:
/// <c>supportedCrs[]</c>, <c>storageCrs</c>, and <c>storageCrsCoordinateEpoch</c>.
/// Does NOT change the stored SRID / geometry type / extent. Null fields leave the
/// corresponding stored value unchanged; an empty <c>supportedCrs</c> array clears it.
/// </summary>
public sealed class LayerSpatialUpdateRequest
{
    /// <summary>Additional CRSes this layer can serve. Null leaves unchanged; empty array clears.</summary>
    public IReadOnlyList<SpatialReferencePayload>? SupportedCrs { get; init; }

    /// <summary>On-disk storage CRS. Null leaves unchanged.</summary>
    public SpatialReferencePayload? StorageCrs { get; init; }

    /// <summary>Whether a supplied <c>StorageCrs</c> should be cleared instead of set.</summary>
    public bool ClearStorageCrs { get; init; }

    /// <summary>Coordinate epoch (decimal year) for time-varying CRSes. Null leaves unchanged.</summary>
    public double? StorageCrsCoordinateEpoch { get; init; }

    /// <summary>Whether the coordinate epoch should be cleared instead of set.</summary>
    public bool ClearStorageCrsCoordinateEpoch { get; init; }
}

/// <summary>Response payload echoing a layer's persisted CRS authoring fields.</summary>
public sealed class LayerSpatialResponse
{
    public int LayerId { get; init; }

    /// <summary>The advertised output spatial reference (read-only here).</summary>
    public SpatialReferencePayload? SpatialReference { get; init; }

    public IReadOnlyList<SpatialReferencePayload> SupportedCrs { get; init; } = Array.Empty<SpatialReferencePayload>();

    public SpatialReferencePayload? StorageCrs { get; init; }

    public double? StorageCrsCoordinateEpoch { get; init; }
}
