// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Styles.Models;

/// <summary>
/// OGC API - Styles conformance declaration.
/// </summary>
public sealed class OgcStylesConformance
{
    /// <summary>
    /// Conformance class URIs implemented by the OGC API - Styles surface.
    /// </summary>
    [JsonPropertyName("conformsTo")]
    public required string[] ConformsTo { get; init; }
}

/// <summary>
/// OGC API - Styles styles list response (<c>GET /ogc/styles</c>).
/// </summary>
public sealed record StylesList
{
    /// <summary>
    /// The styles available on the server.
    /// </summary>
    [JsonPropertyName("styles")]
    public required ImmutableArray<StyleEntry> Styles { get; init; }

    /// <summary>
    /// Optional default style identifier.
    /// </summary>
    [JsonPropertyName("default")]
    public string? Default { get; init; }

    /// <summary>
    /// Links to related resources (self, alternate, etc.).
    /// </summary>
    [JsonPropertyName("links")]
    public ImmutableArray<Link>? Links { get; init; }
}

/// <summary>
/// A single entry in the OGC API - Styles styles list.
/// </summary>
public sealed record StyleEntry
{
    /// <summary>
    /// Stable style identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable title for the style.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Links to the stylesheet encodings and style metadata.
    /// </summary>
    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

/// <summary>
/// OGC API - Styles style metadata response (<c>GET /ogc/styles/{styleId}/metadata</c>).
/// </summary>
public sealed record StyleMetadataResponse
{
    /// <summary>
    /// Stable style identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable title for the style.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Description of the style.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Keywords describing the style.
    /// </summary>
    [JsonPropertyName("keywords")]
    public ImmutableArray<string>? Keywords { get; init; }

    /// <summary>
    /// License identifier for the style, when known.
    /// </summary>
    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>
    /// Style revision number, expressed as a string, when known.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>
    /// Links incl. the stylesheet encodings, the schema (describedby), and a preview.
    /// </summary>
    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}
