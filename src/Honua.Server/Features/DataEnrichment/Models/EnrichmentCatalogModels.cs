// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.DataEnrichment.Models;

internal sealed class EnrichmentDatasetsResponse
{
    [JsonPropertyName("datasets")]
    public EnrichmentDatasetMetadata[] Datasets { get; set; } = [];
}

internal sealed class EnrichmentDatasetMetadata
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("layerId")]
    public int LayerId { get; set; }

    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; set; }

    [JsonPropertyName("attributes")]
    public string[] Attributes { get; set; } = [];

    [JsonPropertyName("defaultPredicate")]
    public string DefaultPredicate { get; set; } = "intersects";

    [JsonPropertyName("distanceMeters")]
    public double? DistanceMeters { get; set; }

    [JsonPropertyName("provenance")]
    public string? Provenance { get; set; }

    // Downstream consumers must surface this attribution string.
    [JsonPropertyName("attribution")]
    public string? Attribution { get; set; }

    [JsonPropertyName("license")]
    public string? License { get; set; }

    [JsonPropertyName("minimumEdition")]
    public string MinimumEdition { get; set; } = "Pro";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "managed";
}

public sealed class RegisterEnrichmentDatasetRequest
{
    public string? Id { get; set; }

    public string? Title { get; set; }

    public string? Category { get; set; }

    public int? LayerId { get; set; }

    public string? GeometryType { get; set; }

    public string[]? Attributes { get; set; }

    public string? DefaultPredicate { get; set; }

    public double? DistanceMeters { get; set; }

    public string? Provenance { get; set; }

    // Downstream consumers must surface this attribution string.
    public string? Attribution { get; set; }

    public string? License { get; set; }

    public string? MinimumEdition { get; set; }
}

public sealed class UpdateEnrichmentDatasetRequest
{
    public string? Title { get; set; }

    public string? Category { get; set; }

    public int? LayerId { get; set; }

    public string? GeometryType { get; set; }

    public string[]? Attributes { get; set; }

    public string? DefaultPredicate { get; set; }

    public double? DistanceMeters { get; set; }

    public string? Provenance { get; set; }

    public string? Attribution { get; set; }

    public string? License { get; set; }

    public string? MinimumEdition { get; set; }
}
