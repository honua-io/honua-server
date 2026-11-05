// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Factory methods improve readability")]
public readonly record struct QueryResult<T>
{
    public QueryResult()
    {
        Items = ImmutableArray<T>.Empty;
    }

    public long TotalCount { get; init; }

    public ImmutableArray<T> Items { get; init; } = ImmutableArray<T>.Empty;

    public bool HasMoreResults { get; init; }

    public ImmutableArray<T> Features
    {
        readonly get => Items;
        init => Items = value;
    }

    public bool ExceededTransferLimit
    {
        readonly get => HasMoreResults;
        init => HasMoreResults = value;
    }

    public static QueryResult<T> Create(long totalCount, ImmutableArray<T> items, bool hasMoreResults = false)
        => new() { TotalCount = totalCount, Items = items, HasMoreResults = hasMoreResults };

    public static QueryResult<T> Empty()
        => new() { TotalCount = 0, Items = ImmutableArray<T>.Empty, HasMoreResults = false };
}
