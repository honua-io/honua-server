// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed record MetadataV2Relationship
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("relatedResourceId")]
    public string RelatedResourceId { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("cardinality")]
    public string Cardinality { get; init; } = "one-to-many";

    [JsonPropertyName("composite")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Composite { get; init; }

    [JsonPropertyName("originField")]
    public string OriginField { get; init; } = string.Empty;

    [JsonPropertyName("destinationField")]
    public string DestinationField { get; init; } = string.Empty;

    [JsonPropertyName("esriRelationshipId")]
    public int? EsriRelationshipId { get; init; }
}
