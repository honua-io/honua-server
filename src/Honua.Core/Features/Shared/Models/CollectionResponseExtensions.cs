// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Extension methods for working with collection responses across different protocols
/// </summary>
public static class CollectionResponseExtensions
{
    /// <summary>
    /// Gets the total count from pagination metadata, if available
    /// </summary>
    /// <typeparam name="T">Type of items in the collection</typeparam>
    /// <param name="response">Collection response</param>
    /// <returns>Total count or null if not available</returns>
    public static long? GetTotalCount<T>(this ICollectionResponse<T> response)
        => response.Pagination?.TotalCount;

    /// <summary>
    /// Gets the number of items returned in the current response
    /// </summary>
    /// <typeparam name="T">Type of items in the collection</typeparam>
    /// <param name="response">Collection response</param>
    /// <returns>Number of returned items</returns>
    public static int GetReturnedCount<T>(this ICollectionResponse<T> response)
        => response.Items.Length;

    /// <summary>
    /// Determines if there are more results available
    /// </summary>
    /// <typeparam name="T">Type of items in the collection</typeparam>
    /// <param name="response">Collection response</param>
    /// <returns>True if more results are available</returns>
    public static bool HasMoreResults<T>(this ICollectionResponse<T> response)
        => response.Pagination?.HasMoreResults ?? false;

    /// <summary>
    /// Gets pagination links from the collection
    /// </summary>
    /// <typeparam name="T">Type of items in the collection</typeparam>
    /// <param name="response">Collection response</param>
    /// <returns>Pagination links</returns>
    public static ImmutableArray<ILink> GetPaginationLinks<T>(this ICollectionResponse<T> response)
    {
        if (response.Links == null)
            return ImmutableArray<ILink>.Empty;

        return response.Links.Value
            .Where(link => link.Rel is "next" or "prev" or "first" or "last")
            .ToImmutableArray();
    }

    /// <summary>
    /// Gets self links from the collection
    /// </summary>
    /// <typeparam name="T">Type of items in the collection</typeparam>
    /// <param name="response">Collection response</param>
    /// <returns>Self links</returns>
    public static ImmutableArray<ILink> GetSelfLinks<T>(this ICollectionResponse<T> response)
    {
        if (response.Links == null)
            return ImmutableArray<ILink>.Empty;

        return response.Links.Value
            .Where(link => link.Rel == "self")
            .ToImmutableArray();
    }

    /// <summary>
    /// Checks if the collection is empty
    /// </summary>
    /// <typeparam name="T">Type of items in the collection</typeparam>
    /// <param name="response">Collection response</param>
    /// <returns>True if the collection contains no items</returns>
    public static bool IsEmpty<T>(this ICollectionResponse<T> response)
        => response.Items.Length == 0;

    /// <summary>
    /// Gets a link of a specific relation type
    /// </summary>
    /// <typeparam name="T">Type of items in the collection</typeparam>
    /// <param name="response">Collection response</param>
    /// <param name="relationType">Relation type to search for</param>
    /// <returns>First link with the specified relation type, or null if not found</returns>
    public static ILink? GetLinkByRelation<T>(this ICollectionResponse<T> response, string relationType)
    {
        if (response.Links == null)
            return null;

        return response.Links.Value.FirstOrDefault(link => link.Rel == relationType);
    }
}
