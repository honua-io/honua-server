// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Console.Models;

public sealed class DcatCatalog
{
    [JsonPropertyName("@context")]
    public string Context { get; init; } = "https://project-open-data.cio.gov/v1.1/schema/catalog.jsonld";

    [JsonPropertyName("@type")]
    public string Type { get; init; } = "dcat:Catalog";

    [JsonPropertyName("conformsTo")]
    public string ConformsTo { get; init; } = "https://project-open-data.cio.gov/v1.1/schema";

    [JsonPropertyName("dataset")]
    public required IReadOnlyList<DcatDataset> Dataset { get; init; }
}

public sealed class DcatDataset
{
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "dcat:Dataset";

    [JsonPropertyName("identifier")]
    public required string Identifier { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("keyword")]
    public IReadOnlyList<string>? Keyword { get; init; }

    [JsonPropertyName("modified")]
    public string? Modified { get; init; }

    [JsonPropertyName("publisher")]
    public DcatPublisher? Publisher { get; init; }

    [JsonPropertyName("contactPoint")]
    public DcatContactPoint? ContactPoint { get; init; }

    [JsonPropertyName("license")]
    public string? License { get; init; }

    [JsonPropertyName("landingPage")]
    public string? LandingPage { get; init; }

    [JsonPropertyName("spatial")]
    public string? Spatial { get; init; }

    [JsonPropertyName("temporal")]
    public string? Temporal { get; init; }

    [JsonPropertyName("accessLevel")]
    public string AccessLevel { get; init; } = "public";

    [JsonPropertyName("distribution")]
    public IReadOnlyList<DcatDistribution>? Distribution { get; init; }
}

public sealed class DcatPublisher
{
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "org:Organization";

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed class DcatContactPoint
{
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "vcard:Contact";

    [JsonPropertyName("fn")]
    public required string Fn { get; init; }

    [JsonPropertyName("hasEmail")]
    public string? HasEmail { get; init; }
}

public sealed class DcatDistribution
{
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "dcat:Distribution";

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("accessURL")]
    public required string AccessUrl { get; init; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; init; }

    [JsonPropertyName("format")]
    public string? Format { get; init; }
}
