// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Console.Models;

// Wire DTOs for the Console catalog discovery-endpoints registry read API
// (honua-server#1279). These shapes mirror the consumer contract defined in
// honua-console's Honua.Console.Contracts/CatalogDiscoveryShims.cs
// (IHonuaCatalogDiscoveryClient) so the live binding activates the moment this
// API publishes. This surface is DISTINCT from the singular content catalog
// (/api/v1/console/content, #1162): it describes *which discovery dialects the
// server publishes to consumers* (Esri catalog, OGC API Records, OData, STAC,
// DCAT), not the content items themselves.

/// <summary>
/// The catalog discovery dialects the registry can expose. Mirrors the
/// honua-console <c>HonuaCatalogDialects</c> string set.
/// </summary>
public static class CatalogDialects
{
    /// <summary>Esri catalog / portal item discovery.</summary>
    public const string Esri = "esri";

    /// <summary>OGC API Records discovery.</summary>
    public const string Ogc = "ogc";

    /// <summary>OData v4 service document discovery.</summary>
    public const string ODataV4 = "odata";

    /// <summary>STAC catalog discovery.</summary>
    public const string Stac = "stac";

    /// <summary>DCAT data-catalog discovery.</summary>
    public const string Dcat = "dcat";
}

/// <summary>
/// How a single catalog-item field is sourced: derived from the backing
/// service/resource, system-managed, or catalog-only operator input.
/// </summary>
public static class CatalogFieldStates
{
    /// <summary>Field is system-managed (identity, immutable).</summary>
    public const string System = "system";

    /// <summary>Field is calculated/derived from a backing resource.</summary>
    public const string Calculated = "calculated";

    /// <summary>Field is catalog-only operator input.</summary>
    public const string Input = "input";
}

/// <summary>One feeder that populates a discovery endpoint (e.g. a FeatureServer or an OGC API collection).</summary>
public sealed record CatalogFeeder
{
    /// <summary>Feeder kind (e.g. <c>feature-server</c>, <c>ogc-features</c>, <c>odata-entity-set</c>).</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>Human-readable feeder label.</summary>
    [JsonPropertyName("label")]
    public required string Label { get; init; }
}

/// <summary>One discovery endpoint card in the registry: dialect, on/off, auto-default vs opt-in, feeders, issues.</summary>
public sealed record CatalogEndpoint
{
    /// <summary>Stable endpoint key used in drill-down routes.</summary>
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    /// <summary>Display title.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>Optional description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Discovery dialect (see <see cref="CatalogDialects"/>).</summary>
    [JsonPropertyName("dialect")]
    public required string Dialect { get; init; }

    /// <summary>Server-wide on/off state.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>True when the endpoint auto-mirrors publications (auto-default-on); false when resources opt in.</summary>
    [JsonPropertyName("autoDefault")]
    public bool AutoDefault { get; init; }

    /// <summary>Public discovery URL, when published.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>Number of catalog entries currently exposed by the endpoint.</summary>
    [JsonPropertyName("entries")]
    public int Entries { get; init; }

    /// <summary>Short summary of what feeds the endpoint.</summary>
    [JsonPropertyName("fedBy")]
    public string? FedBy { get; init; }

    /// <summary>The feeders that populate the endpoint.</summary>
    [JsonPropertyName("feeders")]
    public IReadOnlyList<CatalogFeeder> Feeders { get; init; } = Array.Empty<CatalogFeeder>();

    /// <summary>Count of per-endpoint validation issues.</summary>
    [JsonPropertyName("issueCount")]
    public int IssueCount { get; init; }
}

/// <summary>The discovery-endpoints registry projection: the endpoint cards plus auto/opt-in aggregate counts.</summary>
public sealed record CatalogDiscoveryRegistry
{
    /// <summary>The workspace this registry belongs to.</summary>
    [JsonPropertyName("workspaceId")]
    public required string WorkspaceId { get; init; }

    /// <summary>Display name of the workspace.</summary>
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; init; }

    /// <summary>Public host that serves the discovery endpoints.</summary>
    [JsonPropertyName("publicHost")]
    public string? PublicHost { get; init; }

    /// <summary>The discovery endpoint cards.</summary>
    [JsonPropertyName("endpoints")]
    public IReadOnlyList<CatalogEndpoint> Endpoints { get; init; } = Array.Empty<CatalogEndpoint>();

    /// <summary>Count of endpoints that auto-default-on.</summary>
    [JsonPropertyName("autoDefaultCount")]
    public int AutoDefaultCount { get; init; }

    /// <summary>Count of endpoints that require resources to opt in.</summary>
    [JsonPropertyName("optInCount")]
    public int OptInCount { get; init; }
}

/// <summary>One row in the endpoint-detail items table: a mirrored publication item.</summary>
public sealed record CatalogEndpointItem
{
    /// <summary>Stable item id used in the item drill-down route.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Item title.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>Backing service the item is mirrored from.</summary>
    [JsonPropertyName("fromService")]
    public string? FromService { get; init; }

    /// <summary>Backing resource reference.</summary>
    [JsonPropertyName("resource")]
    public string? Resource { get; init; }

    /// <summary>Catalog tags.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>True when the item carries a thumbnail.</summary>
    [JsonPropertyName("hasThumbnail")]
    public bool HasThumbnail { get; init; }

    /// <summary>Catalog-presentation license string.</summary>
    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>Count of per-item validation issues.</summary>
    [JsonPropertyName("issueCount")]
    public int IssueCount { get; init; }

    /// <summary>Last-updated timestamp (ISO-8601), when known.</summary>
    [JsonPropertyName("updated")]
    public string? Updated { get; init; }
}

/// <summary>The endpoint-detail projection: the endpoint header facts plus the mirrored items table.</summary>
public sealed record CatalogEndpointDetail
{
    /// <summary>The endpoint card.</summary>
    [JsonPropertyName("endpoint")]
    public required CatalogEndpoint Endpoint { get; init; }

    /// <summary>Last rebuild timestamp (ISO-8601), when known.</summary>
    [JsonPropertyName("lastRebuild")]
    public string? LastRebuild { get; init; }

    /// <summary>True when the endpoint auto-mirrors publications.</summary>
    [JsonPropertyName("autoMirror")]
    public bool AutoMirror { get; init; }

    /// <summary>The mirrored items.</summary>
    [JsonPropertyName("items")]
    public IReadOnlyList<CatalogEndpointItem> Items { get; init; } = Array.Empty<CatalogEndpointItem>();
}

/// <summary>One field in the catalog item editor with its source state and (for derived fields) its origin.</summary>
public sealed record CatalogItemField
{
    /// <summary>Field label.</summary>
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    /// <summary>Field source state (see <see cref="CatalogFieldStates"/>).</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    /// <summary>Field value.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    /// <summary>For derived fields, the resource/service the value is derived from.</summary>
    [JsonPropertyName("derivedFrom")]
    public string? DerivedFrom { get; init; }
}

/// <summary>A field group in the catalog item editor (Identity, Catalog presentation, Service bindings, Standards mapping).</summary>
public sealed record CatalogItemFieldGroup
{
    /// <summary>Group title.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>Optional group subtitle.</summary>
    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; init; }

    /// <summary>Optional scope hint (e.g. <c>resource-derived</c>, <c>catalog-only</c>).</summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    /// <summary>The fields in this group.</summary>
    [JsonPropertyName("fields")]
    public IReadOnlyList<CatalogItemField> Fields { get; init; } = Array.Empty<CatalogItemField>();
}

/// <summary>The catalog item editor projection: an auto-mirrored item with its derived/catalog/binding/standards groups.</summary>
public sealed record CatalogItem
{
    /// <summary>Stable item id.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Item title.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>True when the item is auto-mirrored from its backing service.</summary>
    [JsonPropertyName("autoMirror")]
    public bool AutoMirror { get; init; }

    /// <summary>True when the item is live/published.</summary>
    [JsonPropertyName("live")]
    public bool Live { get; init; }

    /// <summary>Number of backing services.</summary>
    [JsonPropertyName("backingServiceCount")]
    public int BackingServiceCount { get; init; }

    /// <summary>Number of catalog tags.</summary>
    [JsonPropertyName("tagCount")]
    public int TagCount { get; init; }

    /// <summary>Count of per-item validation issues.</summary>
    [JsonPropertyName("issueCount")]
    public int IssueCount { get; init; }

    /// <summary>The field groups.</summary>
    [JsonPropertyName("groups")]
    public IReadOnlyList<CatalogItemFieldGroup> Groups { get; init; } = Array.Empty<CatalogItemFieldGroup>();
}
