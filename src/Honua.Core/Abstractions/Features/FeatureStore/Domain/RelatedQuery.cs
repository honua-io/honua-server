// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Linq;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct RelatedQuery
{
    // Callers bucket by the stamped origin id, not the destination key, because those keys are not the origin object id.
    public const string OriginObjectIdsAttribute = "__honua_related_origin_ids";

    public required long[] ObjectIds { get; init; }

    public int? RelatedLayerId { get; init; }

    public string? OriginForeignKeyField { get; init; }

    public string? DestinationForeignKeyField { get; init; }

    public string? Where { get; init; }

    public SqlFragment? SqlFilter { get; init; }

    public ImmutableArray<string>? OutFields { get; init; }

    public int? Limit { get; init; }

    public int? Offset { get; init; }

    public static RelatedQuery ForObjects(
        long[] objectIds,
        int relatedLayerId,
        string originForeignKeyField,
        string destinationForeignKeyField)
        => new()
        {
            ObjectIds = objectIds,
            RelatedLayerId = relatedLayerId,
            OriginForeignKeyField = originForeignKeyField,
            DestinationForeignKeyField = destinationForeignKeyField
        };

    public static int? ProbeLimit(int? pageLimit)
        => pageLimit is int limit && limit > 0 && limit < int.MaxValue ? limit + 1 : pageLimit;

    public static QueryResult<T> ApplyProbeWindow<T>(int? pageLimit, IReadOnlyList<T> rows, bool readerHasMore = false)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (pageLimit is not int limit || limit <= 0)
        {
            return rows.Count == 0
                ? QueryResult<T>.Empty()
                : QueryResult<T>.Create(rows.Count, rows.ToImmutableArray(), readerHasMore);
        }

        var hasMore = readerHasMore || rows.Count > limit;
        if (rows.Count == 0)
        {
            return hasMore
                ? QueryResult<T>.Create(0, ImmutableArray<T>.Empty, true)
                : QueryResult<T>.Empty();
        }

        if (!hasMore)
        {
            return QueryResult<T>.Create(rows.Count, rows.ToImmutableArray(), false);
        }

        var kept = rows.Take(limit).ToImmutableArray();
        return QueryResult<T>.Create(kept.Length, kept, true);
    }
}
