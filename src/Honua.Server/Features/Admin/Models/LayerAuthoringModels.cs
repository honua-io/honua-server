// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

public sealed class LayerRelationshipBatchUpdateRequest
{
    public IReadOnlyList<LayerRelationshipBatchUpdateItem> Layers { get; init; } = Array.Empty<LayerRelationshipBatchUpdateItem>();
}

public sealed class LayerRelationshipBatchUpdateItem
{
    public required int StorageLayerId { get; init; }

    public IReadOnlyList<LayerRelationshipUpdateItem> Relationships { get; init; } = Array.Empty<LayerRelationshipUpdateItem>();
}

public sealed class LayerRelationshipBatchResponse
{
    public IReadOnlyList<LayerRelationshipResponse> Layers { get; init; } = Array.Empty<LayerRelationshipResponse>();
}

public sealed class LayerRelationshipUpdateRequest
{
    public IReadOnlyList<LayerRelationshipUpdateItem> Relationships { get; init; } = Array.Empty<LayerRelationshipUpdateItem>();
}

public sealed class LayerRelationshipUpdateItem
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required int RelatedStorageLayerId { get; init; }

    public string Role { get; init; } = "origin";

    public string Cardinality { get; init; } = "one-to-many";

    public required string OriginField { get; init; }

    public required string DestinationField { get; init; }

    [JsonPropertyName("esriRelationshipId")]
    public int? GeoServicesRelationshipId { get; init; }

    // A composite relationship keeps both ends declared and read-only.
    public bool Composite { get; init; }
}

public sealed class LayerRelationshipResponse
{
    public int StorageLayerId { get; init; }

    public IReadOnlyList<LayerRelationshipItem> Relationships { get; init; } = Array.Empty<LayerRelationshipItem>();
}

public sealed class LayerRelationshipItem
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public int RelatedStorageLayerId { get; init; }

    public required string Role { get; init; }

    public required string Cardinality { get; init; }

    public required string OriginField { get; init; }

    public required string DestinationField { get; init; }

    [JsonPropertyName("esriRelationshipId")]
    public int? GeoServicesRelationshipId { get; init; }

    public bool Composite { get; init; }
}

public sealed class LayerAuthoringDocumentResponse
{
    public int StorageLayerId { get; init; }

    public System.Text.Json.JsonElement? Document { get; init; }
}
