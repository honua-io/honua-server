// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.DataEnrichment.Models;

internal sealed class EnrichmentCatalogResponse
{
    [JsonPropertyName("datasets")]
    public EnrichmentDatasetDescriptor[] Datasets { get; set; } = [];
}

internal sealed class EnrichmentDatasetDescriptor
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("defaultPredicate")]
    public string DefaultPredicate { get; set; } = "intersects";

    [JsonPropertyName("attributes")]
    public string[] Attributes { get; set; } = [];
}

internal sealed class EnrichmentRequest
{
    [JsonPropertyName("datasetId")]
    public string? DatasetId { get; set; }

    [JsonPropertyName("datasetKey")]
    public string? DatasetKey { get; set; }

    [JsonPropertyName("sourceLayerId")]
    public int? SourceLayerId { get; set; }

    [JsonPropertyName("where")]
    public string? Where { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("predicate")]
    public string? Predicate { get; set; }

    // The distance is required when the predicate is dwithin and the dataset declares no default.
    [JsonPropertyName("distanceMeters")]
    public double? DistanceMeters { get; set; }

    [JsonPropertyName("outputFields")]
    public string[]? OutputFields { get; set; }

    [JsonPropertyName("attributes")]
    public string[]? Attributes { get; set; }

    [JsonPropertyName("aggregates")]
    public EnrichmentAggregate[]? Aggregates { get; set; }

    [JsonPropertyName("features")]
    public System.Text.Json.JsonElement? Features { get; set; }
}

internal sealed class EnrichmentAggregate
{
    [JsonPropertyName("statisticType")]
    public string? StatisticType { get; set; }

    [JsonPropertyName("onField")]
    public string? OnField { get; set; }

    [JsonPropertyName("outName")]
    public string? OutName { get; set; }
}
