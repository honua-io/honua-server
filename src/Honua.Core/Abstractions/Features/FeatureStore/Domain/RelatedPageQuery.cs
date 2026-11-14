// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct RelatedPageCursor(long OriginId, long? RelatedId);

public readonly record struct RelatedFeaturePosition(long OriginId, Feature? RelatedFeature);

public readonly record struct RelatedFeaturePage(ImmutableArray<RelatedFeaturePosition> Positions, bool HasMore);

public readonly record struct RelatedPageQuery(RelatedQuery Relationship, int PositionLimit, RelatedPageCursor? After = null)
{
    public const int MaximumOrigins = 1000;
    public const int MaximumPositions = 1000;

    public void Validate()
    {
        if (Relationship.ObjectIds is not { Length: > 0 and <= MaximumOrigins } ||
            Relationship.ObjectIds.Distinct().Count() != Relationship.ObjectIds.Length)
        {
            throw new ArgumentException("Related pages require distinct, bounded origin identifiers.");
        }
        if (PositionLimit is < 1 or > MaximumPositions)
        {
            throw new ArgumentOutOfRangeException(nameof(PositionLimit));
        }
        if (Relationship.RelatedLayerId is null || string.IsNullOrWhiteSpace(Relationship.OriginForeignKeyField) ||
            string.IsNullOrWhiteSpace(Relationship.DestinationForeignKeyField))
        {
            throw new ArgumentException("Related pages require an explicit related layer and both join fields.");
        }
        if (Relationship.Limit is not null || Relationship.Offset is not null)
        {
            throw new ArgumentException("Related pages use a position limit and keyset cursor.");
        }
        if (After is { } cursor && !Relationship.ObjectIds.Contains(cursor.OriginId))
        {
            throw new ArgumentException("The cursor origin must belong to the requested origins.");
        }
    }
}
