// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Relationship metadata for a layer-to-layer association.
/// </summary>
public sealed class LayerRelationshipInfo
{
    /// <summary>
    /// Relationship identifier.
    /// </summary>
    public required int Id { get; init; }

    /// <summary>
    /// Relationship display name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Identifier of the related table or layer.
    /// </summary>
    public required int RelatedTableId { get; init; }

    /// <summary>
    /// Relationship role as reported by the catalog.
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// Esri relationship cardinality enum (e.g. <c>esriRelCardinalityOneToMany</c>).
    /// </summary>
    public string Cardinality { get; init; } = "esriRelCardinalityOneToMany";

    /// <summary>
    /// Whether this is a composite relationship (lifetime of related rows is bound to origin).
    /// </summary>
    public bool Composite { get; init; }

    /// <summary>
    /// Field name used as the relationship key.
    /// </summary>
    public required string KeyField { get; init; }

    /// <summary>
    /// Field name in the origin layer used to match related records.
    /// </summary>
    public required string OriginKeyField { get; init; }

    /// <summary>
    /// Field name in the destination/related layer used to match records.
    /// </summary>
    public required string DestinationKeyField { get; init; }

    /// <summary>
    /// Optional relationship description.
    /// </summary>
    public string? Description { get; init; }
}
