// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Caching;

public sealed class CacheEntryMetadata<T> where T : class
{
    public T? Value { get; }

    public TimeSpan RemainingTtl { get; }

    public bool HasValue => Value is not null;

    public CacheEntryMetadata(T? value, TimeSpan remainingTtl)
    {
        Value = value;
        RemainingTtl = remainingTtl;
    }

    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
        Justification = "Factory method pattern for cache miss is ergonomic and well-understood")]
    public static CacheEntryMetadata<T> Miss() => MissInstance;

    private static readonly CacheEntryMetadata<T> MissInstance = new(null, TimeSpan.Zero);
}
