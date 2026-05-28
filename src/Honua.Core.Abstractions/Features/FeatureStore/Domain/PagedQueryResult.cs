// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Result of a paged feature query when the total count may be omitted.
/// </summary>
/// <typeparam name="T">Type of items in the result set.</typeparam>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Factory methods improve readability")]
public readonly record struct PagedQueryResult<T>
{
    /// <summary>
    /// Initializes a paged query result with empty items to keep default struct instances safe.
    /// </summary>
    public PagedQueryResult()
    {
        Items = ImmutableArray<T>.Empty;
    }

    /// <summary>
    /// Total number of records available when known.
    /// </summary>
    public long? TotalCount { get; init; }

    /// <summary>
    /// Items in the current page.
    /// </summary>
    public ImmutableArray<T> Items { get; init; } = ImmutableArray<T>.Empty;

    /// <summary>
    /// Whether there are more results beyond the current page.
    /// </summary>
    public bool HasMoreResults { get; init; }

    /// <summary>
    /// Creates a paged query result.
    /// </summary>
    /// <param name="items">Items in the current page.</param>
    /// <param name="hasMoreResults">Whether more results exist.</param>
    /// <param name="totalCount">Optional total count when known.</param>
    /// <returns>New paged query result instance.</returns>
    public static PagedQueryResult<T> Create(
        ImmutableArray<T> items,
        bool hasMoreResults = false,
        long? totalCount = null)
        => new() { TotalCount = totalCount, Items = items, HasMoreResults = hasMoreResults };

    /// <summary>
    /// Creates an empty paged query result.
    /// </summary>
    /// <returns>Empty paged query result.</returns>
    public static PagedQueryResult<T> Empty()
        => new() { TotalCount = null, Items = ImmutableArray<T>.Empty, HasMoreResults = false };
}
