// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Request payload for replacing a layer's relationships. The supplied set replaces all existing
/// relationships on the layer's resource.
/// </summary>
public sealed class LayerRelationshipUpdateRequest
{
    public IReadOnlyList<LayerRelationshipUpdateItem> Relationships { get; init; } = Array.Empty<LayerRelationshipUpdateItem>();
}

/// <summary>A single relationship to author on a layer. The related layer is given by its (global) layer id.</summary>
public sealed class LayerRelationshipUpdateItem
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Global layer id of the related layer (resolved to its resource id server-side).</summary>
    public required int RelatedLayerId { get; init; }

    /// <summary>Typically "origin" or "destination".</summary>
    public string Role { get; init; } = "origin";

    /// <summary>"one-to-one", "one-to-many", or "many-to-many".</summary>
    public string Cardinality { get; init; } = "one-to-many";

    public required string OriginField { get; init; }

    public required string DestinationField { get; init; }

    public int? EsriRelationshipId { get; init; }
}

/// <summary>Response payload for a layer's persisted relationships.</summary>
public sealed class LayerRelationshipResponse
{
    public int LayerId { get; init; }

    public IReadOnlyList<LayerRelationshipItem> Relationships { get; init; } = Array.Empty<LayerRelationshipItem>();
}

/// <summary>A persisted relationship, with the related resource resolved back to its (global) layer id.</summary>
public sealed class LayerRelationshipItem
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public int RelatedLayerId { get; init; }

    public required string Role { get; init; }

    public required string Cardinality { get; init; }

    public required string OriginField { get; init; }

    public required string DestinationField { get; init; }

    public int? EsriRelationshipId { get; init; }
}

/// <summary>Response payload echoing a layer's stored popup-info / drawing-info JSON template.</summary>
public sealed class LayerAuthoringDocumentResponse
{
    public int LayerId { get; init; }

    public System.Text.Json.JsonElement? Document { get; init; }
}
