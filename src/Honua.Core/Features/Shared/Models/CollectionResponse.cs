// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Common data structure for API responses containing collections of items with optional links and pagination metadata.
/// Designed for composition rather than inheritance to preserve protocol-specific JSON property names.
/// Provides a standardized internal structure shared across different protocols (OGC, FeatureServer, OData).
/// </summary>
/// <typeparam name="T">Type of items in the collection</typeparam>
public readonly record struct CollectionResponseData<T>
{
    /// <summary>
    /// Collection of items in the response
    /// </summary>
    public required ImmutableArray<T> Items { get; init; }

    /// <summary>
    /// Links to related resources (pagination, self, alternate representations, etc.)
    /// Optional - not all protocols require links
    /// </summary>
    public ImmutableArray<ILink>? Links { get; init; }

    /// <summary>
    /// Pagination metadata including total count and navigation information
    /// Optional - only included when pagination is relevant
    /// </summary>
    public IPaginationMetadata? Pagination { get; init; }

    /// <summary>
    /// Creates collection response data
    /// </summary>
    /// <param name="items">Collection of items</param>
    /// <param name="links">Optional links to related resources</param>
    /// <param name="pagination">Optional pagination metadata</param>
    /// <returns>New collection response data instance</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Factory method improves readability")]
    public static CollectionResponseData<T> Create(
        ImmutableArray<T> items,
        ImmutableArray<ILink>? links = null,
        IPaginationMetadata? pagination = null)
        => new() { Items = items, Links = links, Pagination = pagination };
}

/// <summary>
/// Interface for collection response types across different protocols.
/// Provides a common contract for accessing collection data, links, and pagination metadata
/// while allowing each protocol to maintain its specific JSON structure.
/// </summary>
/// <typeparam name="T">Type of items in the collection</typeparam>
public interface ICollectionResponse<T>
{
    /// <summary>
    /// Collection of items in the response
    /// </summary>
    ImmutableArray<T> Items { get; }

    /// <summary>
    /// Links to related resources (pagination, self, alternate representations, etc.)
    /// Optional - not all protocols require links
    /// </summary>
    ImmutableArray<ILink>? Links { get; }

    /// <summary>
    /// Pagination metadata including total count and navigation information
    /// Optional - only included when pagination is relevant
    /// </summary>
    IPaginationMetadata? Pagination { get; }
}

/// <summary>
/// Interface for link objects across different protocols
/// Allows each protocol to define its own Link implementation while sharing common structure
/// </summary>
public interface ILink
{
    /// <summary>
    /// The URI of the linked resource
    /// </summary>
    string Href { get; }

    /// <summary>
    /// Relation type (e.g., "self", "next", "prev", "alternate")
    /// </summary>
    string? Rel { get; }

    /// <summary>
    /// MIME type of the linked resource
    /// </summary>
    string? Type { get; }

    /// <summary>
    /// Human-readable title for the link
    /// </summary>
    string? Title { get; }
}

/// <summary>
/// Interface for pagination metadata across different protocols
/// Allows each protocol to define its own pagination implementation while sharing common structure
/// </summary>
public interface IPaginationMetadata
{
    /// <summary>
    /// Total number of items available (before pagination)
    /// </summary>
    long? TotalCount { get; }

    /// <summary>
    /// Number of items returned in the current response
    /// </summary>
    int? ReturnedCount { get; }

    /// <summary>
    /// Whether there are more results available
    /// </summary>
    bool HasMoreResults { get; }
}

/// <summary>
/// Generic implementation of IPaginationMetadata for simple use cases
/// </summary>
public sealed record PaginationMetadata : IPaginationMetadata
{
    /// <summary>
    /// Total number of items available (before pagination)
    /// </summary>
    public long? TotalCount { get; init; }

    /// <summary>
    /// Number of items returned in the current response
    /// </summary>
    public int? ReturnedCount { get; init; }

    /// <summary>
    /// Whether there are more results available
    /// </summary>
    public bool HasMoreResults { get; init; }

    /// <summary>
    /// Creates pagination metadata
    /// </summary>
    /// <param name="totalCount">Total number of items available</param>
    /// <param name="returnedCount">Number of items in current response</param>
    /// <param name="hasMoreResults">Whether more results are available</param>
    /// <returns>New pagination metadata instance</returns>
    public static PaginationMetadata Create(long? totalCount, int? returnedCount, bool hasMoreResults = false)
        => new() { TotalCount = totalCount, ReturnedCount = returnedCount, HasMoreResults = hasMoreResults };
}
