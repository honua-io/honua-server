// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

// The page never changes the item's access tier.
public sealed record ConsoleOpenDataPage
{
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("publisherName")]
    public string? PublisherName { get; init; }

    [JsonPropertyName("contactName")]
    public string? ContactName { get; init; }

    [JsonPropertyName("contactEmail")]
    public string? ContactEmail { get; init; }

    [JsonPropertyName("license")]
    public string? License { get; init; }

    [JsonPropertyName("landingPage")]
    public string? LandingPage { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    [JsonPropertyName("distributions")]
    public IReadOnlyList<ConsoleOpenDataDistribution> Distributions { get; init; } = Array.Empty<ConsoleOpenDataDistribution>();

    [JsonPropertyName("spatialExtent")]
    public ConsoleSpatialExtent? SpatialExtent { get; init; }

    [JsonPropertyName("temporalExtent")]
    public ConsoleTemporalExtent? TemporalExtent { get; init; }

    [JsonPropertyName("provenanceRefs")]
    public IReadOnlyList<string> ProvenanceRefs { get; init; } = Array.Empty<string>();

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("updatedById")]
    public string? UpdatedById { get; init; }
}

public sealed record ConsoleOpenDataDistribution
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("accessUrl")]
    public required string AccessUrl { get; init; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; init; }

    [JsonPropertyName("format")]
    public string? Format { get; init; }
}

public sealed record ConsoleSpatialExtent
{
    [JsonPropertyName("west")]
    public required double West { get; init; }

    [JsonPropertyName("south")]
    public required double South { get; init; }

    [JsonPropertyName("east")]
    public required double East { get; init; }

    [JsonPropertyName("north")]
    public required double North { get; init; }
}

public sealed record ConsoleTemporalExtent
{
    [JsonPropertyName("start")]
    public DateTimeOffset? Start { get; init; }

    [JsonPropertyName("end")]
    public DateTimeOffset? End { get; init; }
}
