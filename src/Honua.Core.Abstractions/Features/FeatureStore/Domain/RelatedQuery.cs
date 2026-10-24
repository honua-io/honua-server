// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Linq;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Represents a query for related features through layer relationships
/// </summary>
public readonly record struct RelatedQuery
{
    /// <summary>
    /// Internal attribute key used to stamp each returned related feature with the
    /// origin object id(s) it belongs to. A related row matches its origin through
    /// the foreign-key fields (<see cref="OriginForeignKeyField"/> →
    /// <see cref="DestinationForeignKeyField"/>), which are NOT necessarily the
    /// origin's object id. Grouping callers must bucket by this stamped origin id
    /// rather than by the destination key value (the latter only matches the object
    /// id when the origin foreign key happens to be the object-id field). The value
    /// is a <c>long[]</c> because a single foreign-key value can resolve to multiple
    /// origin object ids. The <c>__</c> prefix marks it internal so it is stripped
    /// from emitted attributes.
    /// </summary>
    public const string OriginObjectIdsAttribute = "__honua_related_origin_ids";

    /// <summary>
    /// Object IDs of the origin features to find related records for
    /// </summary>
    public required long[] ObjectIds { get; init; }

    /// <summary>
    /// Storage layer identifier of the related resource.
    /// </summary>
    public int? RelatedLayerId { get; init; }

    /// <summary>
    /// Field on the source resource whose values are used to locate related rows.
    /// </summary>
    public string? OriginForeignKeyField { get; init; }

    /// <summary>
    /// Field on the related resource matched against the source values.
    /// </summary>
    public string? DestinationForeignKeyField { get; init; }

    /// <summary>
    /// WHERE clause filter expression for the related records (GeoServices REST SQL syntax)
    /// </summary>
    public string? Where { get; init; }

    /// <summary>
    /// Translated SQL filter fragment for related records.
    /// </summary>
    public SqlFragment? SqlFilter { get; init; }

    /// <summary>
    /// Fields to return from related records (null means all fields)
    /// </summary>
    public ImmutableArray<string>? OutFields { get; init; }

    /// <summary>
    /// Maximum number of related records to return across every origin object.
    /// The SQL window is global, not per origin.
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>
    /// Number of related records to skip for pagination
    /// </summary>
    public int? Offset { get; init; }

    /// <summary>
    /// Creates a related query without requiring protocol-specific relationship metadata.
    /// </summary>
    /// <param name="objectIds">Origin object IDs</param>
    /// <param name="relatedLayerId">Storage layer identifier of the related resource</param>
    /// <param name="originForeignKeyField">Source resource field to read</param>
    /// <param name="destinationForeignKeyField">Related resource field to match</param>
    /// <returns>Related query instance</returns>
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

    /// <summary>
    /// Limit that fetches one row past the page so a store can set <see cref="QueryResult{T}.HasMoreResults"/>.
    /// </summary>
    /// <param name="pageLimit">Caller page size. Null and non-positive values are unchanged.</param>
    /// <returns>The probe limit, or <paramref name="pageLimit"/> when no extra row can be requested.</returns>
    public static int? ProbeLimit(int? pageLimit)
        => pageLimit is int limit && limit > 0 && limit < int.MaxValue ? limit + 1 : pageLimit;

    /// <summary>
    /// Trims a probe page back to <paramref name="pageLimit"/> and reports whether another row existed.
    /// </summary>
    /// <typeparam name="T">Row type.</typeparam>
    /// <param name="pageLimit">Requested page size. Null keeps every row.</param>
    /// <param name="rows">Rows fetched with <see cref="ProbeLimit"/>.</param>
    /// <param name="readerHasMore">True when an upstream reader already observed rows beyond the probe.</param>
    /// <returns>The page and the exceeded-transfer flag.</returns>
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
