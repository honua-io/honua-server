// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Result of a feature query with pagination support
/// </summary>
/// <typeparam name="T">Type of items in the result set</typeparam>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Factory methods improve readability")]
public readonly record struct QueryResult<T>
{
    /// <summary>
    /// Total number of records available (before pagination)
    /// </summary>
    public required long TotalCount { get; init; }

    /// <summary>
    /// Features in the current page
    /// </summary>
    public required ImmutableArray<T> Items { get; init; }

    /// <summary>
    /// Whether there are more results beyond the current page
    /// </summary>
    public bool HasMoreResults { get; init; }

    /// <summary>
    /// Creates a query result
    /// </summary>
    /// <param name="totalCount">Total number of available records</param>
    /// <param name="items">Items in current page</param>
    /// <param name="hasMoreResults">Whether more results exist</param>
    /// <returns>New query result instance</returns>
    public static QueryResult<T> Create(long totalCount, ImmutableArray<T> items, bool hasMoreResults = false)
        => new() { TotalCount = totalCount, Items = items, HasMoreResults = hasMoreResults };

    /// <summary>
    /// Creates an empty query result
    /// </summary>
    /// <returns>Empty query result</returns>
    public static QueryResult<T> Empty()
        => new() { TotalCount = 0, Items = ImmutableArray<T>.Empty, HasMoreResults = false };
}
