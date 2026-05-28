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
    private IReadOnlyList<string>? _tags;
    private IReadOnlyDictionary<string, string>? _labels;
    private IReadOnlyDictionary<string, string>? _annotations;
    private IReadOnlyList<string>? _keywords;
    private IReadOnlyList<string>? _themes;
    private IReadOnlyList<MetadataV2Link>? _links;

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
    /// Optional namespace for grouping entities.
    /// </summary>
    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

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
    /// Tags for discovery.
    /// </summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags
    {
        get => _tags ?? Array.Empty<string>();
        init => _tags = value;
    }

    /// <summary>
    /// Labels for selection and grouping (Kubernetes-style selectable key/values).
    /// Discovery / search tooling reads these. Express a "tag" as a label with an
    /// empty value: <c>{"public": "", "weather": ""}</c>.
    /// </summary>
    [JsonPropertyName("labels")]
    public IReadOnlyDictionary<string, string> Labels
    {
        get => _labels ?? EmptyStringMap;
        init => _labels = value;
    }

    /// <summary>
    /// Tooling annotations (Kubernetes-style opaque key/values; not selectable).
    /// </summary>
    [JsonPropertyName("annotations")]
    public IReadOnlyDictionary<string, string> Annotations
    {
        get => _annotations ?? EmptyStringMap;
        init => _annotations = value;
    }

    /// <summary>
    /// Entity generation for optimistic concurrency.
    /// </summary>
    [JsonPropertyName("generation")]
    public long? Generation { get; init; }

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
    public IReadOnlyList<string> Keywords
    {
        get => _keywords ?? Array.Empty<string>();
        init => _keywords = value;
    }

    /// <summary>
    /// DCAT-style themes/categories (theme URIs or labels). Mapped to
    /// OGC-API-Records.themes, DCAT dcat:theme, and STAC summaries["theme"]
    /// where present.
    /// </summary>
    [JsonPropertyName("themes")]
    public IReadOnlyList<string> Themes
    {
        get => _themes ?? Array.Empty<string>();
        init => _themes = value;
    }

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
    public IReadOnlyList<MetadataV2Link> Links
    {
        get => _links ?? Array.Empty<MetadataV2Link>();
        init => _links = value;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyStringMap =
        new Dictionary<string, string>();
}

/// <summary>
/// Lifecycle and observed status shared by Metadata v2 graph entities.
/// </summary>
public sealed record MetadataV2Status
{
    /// <summary>
    /// Desired or declared lifecycle state.
    /// </summary>
    [JsonPropertyName("lifecycle")]
    public MetadataV2LifecycleStatus Lifecycle { get; init; } = MetadataV2LifecycleStatus.Draft;

    /// <summary>
    /// Observed operational state.
    /// </summary>
    [JsonPropertyName("state")]
    public MetadataV2OperationalState State { get; init; } = MetadataV2OperationalState.Unknown;

    /// <summary>
    /// Reconciliation or validation conditions.
    /// </summary>
    [JsonPropertyName("conditions")]
    public IReadOnlyList<MetadataV2Condition> Conditions { get; init; } = Array.Empty<MetadataV2Condition>();

    /// <summary>
    /// Last observed timestamp.
    /// </summary>
    [JsonPropertyName("observedAt")]
    public DateTimeOffset? ObservedAt { get; init; }
}

/// <summary>
/// A status condition attached to a Metadata v2 entity.
/// </summary>
public sealed record MetadataV2Condition
{
    /// <summary>
    /// Condition type.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// Condition status.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// Machine-readable reason.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>
    /// Human-readable message.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>
    /// Last transition timestamp.
    /// </summary>
    [JsonPropertyName("lastTransitionAt")]
    public DateTimeOffset? LastTransitionAt { get; init; }
}

/// <summary>
/// Named extension point for Metadata v2 tools and plugins.
/// </summary>
public sealed record MetadataV2ExtensionPoint
{
    /// <summary>
    /// Extension point identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Graph entity kinds this extension point applies to.
    /// </summary>
    [JsonPropertyName("appliesTo")]
    public IReadOnlyList<string> AppliesTo { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Optional description of the extension point.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Optional JSON schema fragment for extension values.
    /// </summary>
    [JsonPropertyName("schema")]
    public JsonElement? Schema { get; init; }
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
    private IReadOnlyList<string>? _semanticRoles;

    /// <summary>
    /// Stable semantic identifier for field-level promotion across environments.
    /// </summary>
    [JsonPropertyName("semanticId")]
    public string? SemanticId { get; init; }

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
    public IReadOnlyList<string> SemanticRoles
    {
        get => _semanticRoles ?? Array.Empty<string>();
        init => _semanticRoles = value;
    }

    /// <summary>
    /// Human-friendly alias. Mapped to Esri-FeatureServer.fields[].alias,
    /// OData Org.OData.Core.V1.Label, OGC-API-Features Part 5
    /// schema.properties[].title (fallback to <see cref="Title"/>), and
    /// GeoPackage gpkg_data_columns.title.
    /// </summary>
    [JsonPropertyName("alias")]
    public string? Alias { get; init; }

    /// <summary>
    /// True when the field is editable through edit-capable services.
    /// Defaults to true to preserve existing behaviour.
    /// </summary>
    [JsonPropertyName("editable")]
    public bool Editable { get; init; } = true;

    /// <summary>
    /// Maximum length for VARCHAR-typed fields. Null for non-string or
    /// unbounded fields.
    /// </summary>
    [JsonPropertyName("length")]
    public int? Length { get; init; }

    /// <summary>
    /// Optional default value used by edit-capable services when no value is
    /// supplied on insert.
    /// </summary>
    [JsonPropertyName("defaultValue")]
    public JsonElement? DefaultValue { get; init; }

    /// <summary>
    /// Optional value domain (coded values or numeric range).
    /// </summary>
    [JsonPropertyName("domain")]
    public MetadataV2FieldDomain? Domain { get; init; }

    /// <summary>
    /// True when the field should be omitted from public protocol metadata and default feature output.
    /// </summary>
    [JsonPropertyName("hidden")]
    public bool Hidden { get; init; }

    /// <summary>
    /// Provider-native SQL type label (e.g. <c>VARCHAR(64)</c>,
    /// <c>NUMERIC(10,2)</c>, <c>TIMESTAMP WITH TIME ZONE</c>). Free-form;
    /// used by $metadata generators and admin diagnostics.
    /// </summary>
    [JsonPropertyName("sqlType")]
    public string? SqlType { get; init; }

    /// <summary>
    /// Extension data for the field.
    /// </summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>
/// Value domain for a <see cref="MetadataV2Field"/> — either an enumerated
/// list of coded values or a numeric range.
/// </summary>
public sealed record MetadataV2FieldDomain
{
    /// <summary>
    /// Stable domain name.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Domain kind. Canonical values: <c>codedValue</c> or <c>range</c>.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// Coded value entries when <see cref="Type"/> is <c>codedValue</c>.
    /// </summary>
    [JsonPropertyName("codedValues")]
    public IReadOnlyList<MetadataV2CodedValue> CodedValues { get; init; } = Array.Empty<MetadataV2CodedValue>();

    /// <summary>
    /// Range endpoints when <see cref="Type"/> is <c>range</c>. Two-element
    /// list ordered [min, max], JSON-typed to match the field's value type.
    /// </summary>
    [JsonPropertyName("range")]
    public IReadOnlyList<JsonElement>? Range { get; init; }

    /// <summary>
    /// Optional merge policy metadata for clients that understand Esri-style domains.
    /// </summary>
    [JsonPropertyName("mergePolicy")]
    public string? MergePolicy { get; init; }

    /// <summary>
    /// Optional split policy metadata for clients that understand Esri-style domains.
    /// </summary>
    [JsonPropertyName("splitPolicy")]
    public string? SplitPolicy { get; init; }
}

/// <summary>
/// Single entry in a <see cref="MetadataV2FieldDomain"/> coded-value list.
/// </summary>
public sealed record MetadataV2CodedValue
{
    /// <summary>Code value, JSON-typed to match the field's value type.</summary>
    [JsonPropertyName("code")]
    public JsonElement Code { get; init; }

    /// <summary>Human-readable display name for the code.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}
