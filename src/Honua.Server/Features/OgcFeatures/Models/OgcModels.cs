// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.OgcFeatures.Models;

/// <summary>
/// OGC API Features landing page response
/// </summary>
public sealed record LandingPage
{
    /// <summary>
    /// Title of the API
    /// </summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>
    /// Description of the API
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Links to related resources
    /// </summary>
    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

/// <summary>
/// Conformance declaration response
/// </summary>
public sealed record ConformanceDeclaration
{
    /// <summary>
    /// List of conformance classes that this API conforms to
    /// </summary>
    [JsonPropertyName("conformsTo")]
    public required ImmutableArray<string> ConformsTo { get; init; }
}

/// <summary>
/// Link object as defined by OGC API Features specification
/// </summary>
public sealed record Link
{
    /// <summary>
    /// The URI of the linked resource
    /// </summary>
    [JsonPropertyName("href")]
    public required string Href { get; init; }

    /// <summary>
    /// Relation type (e.g., "self", "alternate", "describedby")
    /// </summary>
    [JsonPropertyName("rel")]
    public string? Rel { get; init; }

    /// <summary>
    /// MIME type of the linked resource
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    /// Language of the linked resource
    /// </summary>
    [JsonPropertyName("hreflang")]
    public string? HrefLang { get; init; }

    /// <summary>
    /// Human-readable title for the link
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Creates a link with required properties
    /// </summary>
    /// <param name="href">The URI of the linked resource</param>
    /// <param name="rel">Relation type</param>
    /// <param name="type">MIME type</param>
    /// <param name="title">Human-readable title</param>
    /// <returns>New link instance</returns>
    public static Link Create(string href, string? rel = null, string? type = null, string? title = null)
        => new() { Href = href, Rel = rel, Type = type, Title = title };
}

/// <summary>
/// Standard OGC API Features relation types
/// </summary>
public static class RelationTypes
{
    /// <summary>
    /// Conveys an identifier for the link's context
    /// </summary>
    public const string Self = "self";

    /// <summary>
    /// Refers to an alternate representation of the same resource
    /// </summary>
    public const string Alternate = "alternate";

    /// <summary>
    /// Refers to a resource that describes the link's context
    /// </summary>
    public const string DescribedBy = "describedby";

    /// <summary>
    /// Refers to a resource that serves as the data source
    /// </summary>
    public const string Data = "data";

    /// <summary>
    /// Indicates the link target provides service documentation
    /// </summary>
    public const string ServiceDoc = "service-doc";

    /// <summary>
    /// Indicates the link target provides service description
    /// </summary>
    public const string ServiceDesc = "service-desc";

    /// <summary>
    /// Indicates the link target provides conformance declaration
    /// </summary>
    public const string Conformance = "conformance";

    /// <summary>
    /// Indicates the link target provides collections metadata
    /// </summary>
    public const string Collections = "data";

    /// <summary>
    /// Indicates the link target provides next page of results
    /// </summary>
    public const string Next = "next";

    /// <summary>
    /// Indicates the link target provides previous page of results
    /// </summary>
    public const string Prev = "prev";
}

/// <summary>
/// Standard media types for OGC API Features
/// </summary>
public static class MediaTypes
{
    /// <summary>
    /// JSON media type
    /// </summary>
    public const string Json = "application/json";

    /// <summary>
    /// GeoJSON media type
    /// </summary>
    public const string GeoJson = "application/geo+json";

    /// <summary>
    /// HTML media type
    /// </summary>
    public const string Html = "text/html";

    /// <summary>
    /// OpenAPI 3.0 specification media type
    /// </summary>
    public const string OpenApi = "application/vnd.oai.openapi+json;version=3.0";
}