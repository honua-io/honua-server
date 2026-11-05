// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

public static class CatalogDialects
{
    public const string Esri = "esri";

    public const string Ogc = "ogc";

    public const string ODataV4 = "odata";

    public const string Stac = "stac";

    public const string Dcat = "dcat";
}

public static class CatalogFieldStates
{
    public const string System = "system";

    public const string Calculated = "calculated";

    public const string Input = "input";
}

public sealed record CatalogFeeder
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }
}

public sealed record CatalogEndpoint
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("dialect")]
    public required string Dialect { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("autoDefault")]
    public bool AutoDefault { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("entries")]
    public int Entries { get; init; }

    [JsonPropertyName("fedBy")]
    public string? FedBy { get; init; }

    [JsonPropertyName("feeders")]
    public IReadOnlyList<CatalogFeeder> Feeders { get; init; } = Array.Empty<CatalogFeeder>();

    [JsonPropertyName("issueCount")]
    public int IssueCount { get; init; }
}

public sealed record CatalogDiscoveryRegistry
{
    [JsonPropertyName("workspaceId")]
    public required string WorkspaceId { get; init; }

    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; init; }

    [JsonPropertyName("publicHost")]
    public string? PublicHost { get; init; }

    [JsonPropertyName("endpoints")]
    public IReadOnlyList<CatalogEndpoint> Endpoints { get; init; } = Array.Empty<CatalogEndpoint>();

    [JsonPropertyName("autoDefaultCount")]
    public int AutoDefaultCount { get; init; }

    [JsonPropertyName("optInCount")]
    public int OptInCount { get; init; }
}

public sealed record CatalogEndpointItem
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("fromService")]
    public string? FromService { get; init; }

    [JsonPropertyName("resource")]
    public string? Resource { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    [JsonPropertyName("hasThumbnail")]
    public bool HasThumbnail { get; init; }

    [JsonPropertyName("license")]
    public string? License { get; init; }

    [JsonPropertyName("issueCount")]
    public int IssueCount { get; init; }

    [JsonPropertyName("updated")]
    public string? Updated { get; init; }
}

public sealed record CatalogEndpointDetail
{
    [JsonPropertyName("endpoint")]
    public required CatalogEndpoint Endpoint { get; init; }

    [JsonPropertyName("lastRebuild")]
    public string? LastRebuild { get; init; }

    [JsonPropertyName("autoMirror")]
    public bool AutoMirror { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<CatalogEndpointItem> Items { get; init; } = Array.Empty<CatalogEndpointItem>();
}

public sealed record CatalogItemField
{
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("derivedFrom")]
    public string? DerivedFrom { get; init; }
}

public sealed record CatalogItemFieldGroup
{
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("fields")]
    public IReadOnlyList<CatalogItemField> Fields { get; init; } = Array.Empty<CatalogItemField>();
}

public sealed record CatalogItem
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("autoMirror")]
    public bool AutoMirror { get; init; }

    [JsonPropertyName("live")]
    public bool Live { get; init; }

    [JsonPropertyName("backingServiceCount")]
    public int BackingServiceCount { get; init; }

    [JsonPropertyName("tagCount")]
    public int TagCount { get; init; }

    [JsonPropertyName("issueCount")]
    public int IssueCount { get; init; }

    [JsonPropertyName("groups")]
    public IReadOnlyList<CatalogItemFieldGroup> Groups { get; init; } = Array.Empty<CatalogItemFieldGroup>();
}
