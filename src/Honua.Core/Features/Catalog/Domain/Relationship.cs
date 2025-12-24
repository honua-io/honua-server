// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Catalog.Domain;

/// <summary>
/// Represents a relationship between two layers in a feature service.
/// Defines how features in one layer are related to features in another layer.
/// </summary>
/// <remarks>
/// Relationships enable querying related features across layers using foreign key relationships.
/// Supports both one-to-many and many-to-one relationship types as defined by the GeoServices REST specification.
/// </remarks>
public readonly record struct Relationship
{
    /// <summary>
    /// The unique identifier for this relationship within the layer
    /// </summary>
    public required int RelationshipId { get; init; }

    /// <summary>
    /// The human-readable name of the relationship
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The ID of the layer that this relationship points to
    /// </summary>
    public required int RelatedLayerId { get; init; }

    /// <summary>
    /// The type of relationship (e.g., "esriRelRoleOrigin", "esriRelRoleDestination")
    /// </summary>
    public required string RelationshipType { get; init; }

    /// <summary>
    /// The field name in the origin layer that contains the foreign key
    /// </summary>
    public required string OriginForeignKeyField { get; init; }

    /// <summary>
    /// The field name in the destination layer that contains the primary key
    /// </summary>
    public required string DestinationForeignKeyField { get; init; }

    /// <summary>
    /// Optional description of the relationship
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Creates a new relationship with the specified properties
    /// </summary>
    public static Relationship Create(
        int relationshipId,
        string name,
        int relatedLayerId,
        string relationshipType,
        string originForeignKeyField,
        string destinationForeignKeyField,
        string? description = null)
    {
        if (relationshipId <= 0)
            throw new ArgumentException("Relationship ID must be positive", nameof(relationshipId));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Relationship name cannot be empty", nameof(name));
        if (relatedLayerId < 0)
            throw new ArgumentException("Related layer ID cannot be negative", nameof(relatedLayerId));
        if (string.IsNullOrWhiteSpace(relationshipType))
            throw new ArgumentException("Relationship type cannot be empty", nameof(relationshipType));
        if (string.IsNullOrWhiteSpace(originForeignKeyField))
            throw new ArgumentException("Origin foreign key field cannot be empty", nameof(originForeignKeyField));
        if (string.IsNullOrWhiteSpace(destinationForeignKeyField))
            throw new ArgumentException("Destination foreign key field cannot be empty", nameof(destinationForeignKeyField));

        return new Relationship
        {
            RelationshipId = relationshipId,
            Name = name,
            RelatedLayerId = relatedLayerId,
            RelationshipType = relationshipType,
            OriginForeignKeyField = originForeignKeyField,
            DestinationForeignKeyField = destinationForeignKeyField,
            Description = description
        };
    }
}
