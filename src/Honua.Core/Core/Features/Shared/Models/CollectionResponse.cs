// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Shared.Models;

public readonly record struct CollectionResponseData<T>
{
    public required ImmutableArray<T> Items { get; init; }

    public ImmutableArray<ILink>? Links { get; init; }

    public IPaginationMetadata? Pagination { get; init; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Factory method improves readability")]
    public static CollectionResponseData<T> Create(
        ImmutableArray<T> items,
        ImmutableArray<ILink>? links = null,
        IPaginationMetadata? pagination = null)
        => new() { Items = items, Links = links, Pagination = pagination };
}

public interface ICollectionResponse<T>
{
    ImmutableArray<T> Items { get; }

    ImmutableArray<ILink>? Links { get; }

    IPaginationMetadata? Pagination { get; }
}

public interface ILink
{
    string Href { get; }

    string? Rel { get; }

    string? Type { get; }

    string? Title { get; }
}

public interface IPaginationMetadata
{
    long? TotalCount { get; }

    int? ReturnedCount { get; }

    bool HasMoreResults { get; }
}

public sealed record PaginationMetadata : IPaginationMetadata
{
    public long? TotalCount { get; init; }

    public int? ReturnedCount { get; init; }

    public bool HasMoreResults { get; init; }

    public static PaginationMetadata Create(long? totalCount, int? returnedCount, bool hasMoreResults = false)
        => new() { TotalCount = totalCount, ReturnedCount = returnedCount, HasMoreResults = hasMoreResults };
}
