// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Constants for Metadata v2 model documents.
/// </summary>
public static class MetadataV2Constants
{
    /// <summary>
    /// Semver version of the graph-document <em>schema</em> (the shape of the JSON
    /// payload itself). Bumped on every breaking field change. Distinct from
    /// <see cref="ApiVersion"/>, which identifies the API group/version that
    /// documents at this schema are exchanged through.
    /// </summary>
    public const string SchemaVersion = "2.0.0-alpha.1";

    /// <summary>
    /// Kubernetes-style group/version identifier for the Metadata v2 API. Used by
    /// graph-aware admin/observability surfaces to advertise which API contract
    /// they speak. Independent of <see cref="SchemaVersion"/> — the same API
    /// version can ship multiple schema revisions inside one major API line.
    /// </summary>
    public const string ApiVersion = "metadata.honua.io/v2alpha1";
}

/// <summary>
/// Common metadata fields shared by Metadata v2 graph entities.
/// </summary>
public sealed record MetadataV2ObjectMetadata
{
    /// <summary>
    /// Stable identifier within the graph.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Machine-friendly name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable display title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Human-readable description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Labels for selection and grouping (Kubernetes-style selectable key/values).
    /// Discovery / search tooling reads these. Express a "tag" as a label with an
    /// empty value: <c>{"public": "", "weather": ""}</c>.
    /// </summary>
    [JsonPropertyName("labels")]
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Tooling annotations (Kubernetes-style opaque key/values; not selectable).
    /// </summary>
    [JsonPropertyName("annotations")]
    public IReadOnlyDictionary<string, string> Annotations { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Timestamp when the entity was created.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>
    /// Timestamp when the entity was updated.
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// Free-form discovery keywords. Mapped to OGC-API-Records.keywords,
    /// STAC.collection.keywords, WMS/WMTS/WFS/WCS &lt;ows:Keywords&gt;, Esri
    /// documentInfo.Keywords (comma-joined), and OData Org.OData.Core.V1.Tags.
    /// </summary>
    [JsonPropertyName("keywords")]
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();

    /// <summary>
    /// DCAT-style themes/categories (theme URIs or labels). Mapped to
    /// OGC-API-Records.themes, DCAT dcat:theme, and STAC summaries["theme"]
    /// where present.
    /// </summary>
    [JsonPropertyName("themes")]
    public IReadOnlyList<string> Themes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// BCP-47 language tag (for example <c>en</c>, <c>en-US</c>, <c>de-CH</c>).
    /// Mapped to OGC-API-Records.language, OGC-API-Common landingPage.language,
    /// and link hreflang.
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; init; }

    /// <summary>
    /// SPDX identifier (for example <c>CC-BY-4.0</c>, <c>MIT</c>, <c>Apache-2.0</c>)
    /// or the literal string <c>proprietary</c>. Mapped to STAC.collection.license
    /// (required), OGC-API-Records.license, and OGC-API rel=license link URLs
    /// derived from the SPDX identifier.
    /// </summary>
    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>
    /// Human-readable attribution / credits string. Mapped to
    /// OGC-API-Features.collection.attribution, STAC providers[].name,
    /// WMS &lt;AttributionURL&gt;, Esri copyrightText, and Esri documentInfo.Credits.
    /// </summary>
    [JsonPropertyName("attribution")]
    public string? Attribution { get; init; }

    /// <summary>
    /// Data producer / source organization. Mapped to OGC-API-Records.publisher,
    /// DCAT dcat:publisher, STAC providers[role=producer], and Esri
    /// documentInfo.Subject (loose mapping).
    /// </summary>
    [JsonPropertyName("publisher")]
    public string? Publisher { get; init; }

    /// <summary>
    /// Point of contact for this entity. Mapped to OGC-API-Common
    /// landingPage.contact, OGC-API-Records.contacts[], and Esri
    /// documentInfo.Author.
    /// </summary>
    [JsonPropertyName("contactPoint")]
    public MetadataV2ContactPoint? ContactPoint { get; init; }

    /// <summary>
    /// External links advertised alongside this entity. Mapped to OGC-API
    /// &amp; STAC <c>links[]</c> arrays.
    /// </summary>
    [JsonPropertyName("links")]
    public IReadOnlyList<MetadataV2Link> Links { get; init; } = Array.Empty<MetadataV2Link>();
}

/// <summary>
/// Point of contact carried on <see cref="MetadataV2ObjectMetadata"/>.
/// </summary>
public sealed record MetadataV2ContactPoint
{
    /// <summary>Display name of the contact (person or organization).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Email address of the contact. Must contain '@' when set.</summary>
    [JsonPropertyName("email")]
    public string? Email { get; init; }

    /// <summary>Contact URL (homepage, contact form, …).</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

/// <summary>
/// External link entry, modelled on RFC 8288 / OGC-API &amp; STAC link objects.
/// </summary>
public sealed record MetadataV2Link
{
    /// <summary>Target URL of the link. Required (non-empty) when emitted.</summary>
    [JsonPropertyName("href")]
    public string Href { get; init; } = string.Empty;

    /// <summary>IANA / OGC link relation type (e.g. <c>self</c>, <c>license</c>,
    /// <c>describedby</c>). Required (non-empty) when emitted.</summary>
    [JsonPropertyName("rel")]
    public string Rel { get; init; } = string.Empty;

    /// <summary>Media type of the linked resource (e.g. <c>application/json</c>).</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Human-readable title of the linked resource.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>BCP-47 language tag of the linked resource.</summary>
    [JsonPropertyName("hreflang")]
    public string? Hreflang { get; init; }
}

/// <summary>
/// Canonical field description owned by a Metadata v2 resource.
/// </summary>
public sealed record MetadataV2Field
{
    /// <summary>
    /// Stable source field name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Canonical field type. String-encoded in JSON for snapshot readability.
    /// </summary>
    [JsonPropertyName("type")]
    public MetadataV2FieldType Type { get; init; } = MetadataV2FieldType.Unknown;

    /// <summary>
    /// Human-readable field title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Human-readable field description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// True when null values are valid for the field.
    /// </summary>
    [JsonPropertyName("nullable")]
    public bool Nullable { get; init; }

    /// <summary>
    /// Semantic role identifiers used by catalog and service projections.
    /// </summary>
    [JsonPropertyName("semanticRoles")]
    public IReadOnlyList<string> SemanticRoles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Extension data for the field.
    /// </summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}
