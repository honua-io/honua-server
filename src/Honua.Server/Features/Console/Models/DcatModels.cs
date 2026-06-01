// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Console.Models;

/// <summary>
/// DCAT-US 3.0 / Project Open Data <c>data.json</c> catalog document. Property
/// names follow the data.json schema (Project Open Data Metadata Schema v1.1 /
/// DCAT-US 3.0) so a published catalog round-trips through standard open-data
/// harvesters.
/// </summary>
/// <remarks>
/// Reference: <c>https://resources.data.gov/resources/dcat-us/</c>. Only the
/// bounded subset Honua maps from Console open-data page state is emitted; absent
/// optional fields are omitted (camelCase, ignore-null).
/// </remarks>
public sealed class DcatCatalog
{
    /// <summary>JSON-LD context URI for the data.json schema.</summary>
    [JsonPropertyName("@context")]
    public string Context { get; init; } = "https://project-open-data.cio.gov/v1.1/schema/catalog.jsonld";

    /// <summary>Metadata schema identifier.</summary>
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "dcat:Catalog";

    /// <summary>Conforms-to schema version URI.</summary>
    [JsonPropertyName("conformsTo")]
    public string ConformsTo { get; init; } = "https://project-open-data.cio.gov/v1.1/schema";

    /// <summary>Catalog datasets.</summary>
    [JsonPropertyName("dataset")]
    public required IReadOnlyList<DcatDataset> Dataset { get; init; }
}

/// <summary>
/// A single DCAT-US dataset entry.
/// </summary>
public sealed class DcatDataset
{
    /// <summary>RDF type.</summary>
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "dcat:Dataset";

    /// <summary>Stable dataset identifier.</summary>
    [JsonPropertyName("identifier")]
    public required string Identifier { get; init; }

    /// <summary>Dataset title.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>Dataset description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Discovery keywords.</summary>
    [JsonPropertyName("keyword")]
    public IReadOnlyList<string>? Keyword { get; init; }

    /// <summary>Last-modified timestamp (ISO-8601).</summary>
    [JsonPropertyName("modified")]
    public string? Modified { get; init; }

    /// <summary>Publishing organization.</summary>
    [JsonPropertyName("publisher")]
    public DcatPublisher? Publisher { get; init; }

    /// <summary>Point of contact.</summary>
    [JsonPropertyName("contactPoint")]
    public DcatContactPoint? ContactPoint { get; init; }

    /// <summary>License URL or SPDX identifier.</summary>
    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>Public landing page URL.</summary>
    [JsonPropertyName("landingPage")]
    public string? LandingPage { get; init; }

    /// <summary>Spatial coverage as a DCAT bbox string (W,S,E,N).</summary>
    [JsonPropertyName("spatial")]
    public string? Spatial { get; init; }

    /// <summary>Temporal coverage as an ISO-8601 interval.</summary>
    [JsonPropertyName("temporal")]
    public string? Temporal { get; init; }

    /// <summary>Access level. Open-data datasets are always <c>public</c>.</summary>
    [JsonPropertyName("accessLevel")]
    public string AccessLevel { get; init; } = "public";

    /// <summary>Distributions/access links.</summary>
    [JsonPropertyName("distribution")]
    public IReadOnlyList<DcatDistribution>? Distribution { get; init; }
}

/// <summary>
/// DCAT publishing organization.
/// </summary>
public sealed class DcatPublisher
{
    /// <summary>RDF type.</summary>
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "org:Organization";

    /// <summary>Organization name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

/// <summary>
/// DCAT point-of-contact (vCard).
/// </summary>
public sealed class DcatContactPoint
{
    /// <summary>RDF type.</summary>
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "vcard:Contact";

    /// <summary>Contact full name.</summary>
    [JsonPropertyName("fn")]
    public required string Fn { get; init; }

    /// <summary>Mailto-prefixed contact email, when an email was supplied.</summary>
    [JsonPropertyName("hasEmail")]
    public string? HasEmail { get; init; }
}

/// <summary>
/// DCAT distribution (access link).
/// </summary>
public sealed class DcatDistribution
{
    /// <summary>RDF type.</summary>
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "dcat:Distribution";

    /// <summary>Distribution title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Access URL.</summary>
    [JsonPropertyName("accessURL")]
    public required string AccessUrl { get; init; }

    /// <summary>IANA media type.</summary>
    [JsonPropertyName("mediaType")]
    public string? MediaType { get; init; }

    /// <summary>Short format label.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }
}
