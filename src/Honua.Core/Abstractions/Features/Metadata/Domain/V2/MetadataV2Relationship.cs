// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

using Honua.Core.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed record MetadataV2Relationship : ExtensibleJsonRecord, IJsonOnDeserializing, IJsonOnDeserialized
{
    private MetadataV2JsonAlias<string> _relatedObjectId;

    void IJsonOnDeserializing.OnDeserializing()
    {
        _relatedObjectId = _relatedObjectId.BeginRead();
    }

    void IJsonOnDeserialized.OnDeserialized()
    {
        _relatedObjectId = _relatedObjectId.CompleteRead();
    }

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("relatedResourceId")]
    public string RelatedResourceId
    {
        get => _relatedObjectId.Value ?? string.Empty;
        set => _relatedObjectId = _relatedObjectId.WithLegacy(value);
    }

    [JsonPropertyName("relatedObjectId")]
    public string RelatedObjectId
    {
        get => RelatedResourceId;
        set => _relatedObjectId = _relatedObjectId.WithCanonical(value);
    }

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
    public int? GeoServicesRelationshipId { get; init; }
}
