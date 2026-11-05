// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Catalog.Domain;

public readonly record struct Relationship
{
    public required int RelationshipId { get; init; }

    public required string Name { get; init; }

    public required int RelatedLayerId { get; init; }

    public required string RelationshipType { get; init; }

    public required string OriginForeignKeyField { get; init; }

    public required string DestinationForeignKeyField { get; init; }

    public string? Description { get; init; }

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
