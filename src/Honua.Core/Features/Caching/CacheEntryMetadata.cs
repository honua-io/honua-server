// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Caching;

/// <summary>
/// Represents a cache entry with its value and expiration metadata.
/// Used by background refresh to determine when a stale-while-revalidate refresh should be enqueued.
/// </summary>
/// <typeparam name="T">The type of the cached value</typeparam>
public sealed class CacheEntryMetadata<T> where T : class
{
    /// <summary>
    /// The cached value, or null if not found.
    /// </summary>
    public T? Value { get; }

    /// <summary>
    /// The remaining time before the cache entry expires.
    /// </summary>
    public TimeSpan RemainingTtl { get; }

    /// <summary>
    /// Whether the entry was found in the cache.
    /// </summary>
    public bool HasValue => Value is not null;

    /// <summary>
    /// Creates a cache entry metadata instance.
    /// </summary>
    /// <param name="value">The cached value</param>
    /// <param name="remainingTtl">Time remaining before expiration</param>
    public CacheEntryMetadata(T? value, TimeSpan remainingTtl)
    {
        Value = value;
        RemainingTtl = remainingTtl;
    }

    /// <summary>
    /// Creates a miss result with no value and zero TTL.
    /// </summary>
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
        Justification = "Factory method pattern for cache miss is ergonomic and well-understood")]
    public static CacheEntryMetadata<T> Miss() => MissInstance;

    private static readonly CacheEntryMetadata<T> MissInstance = new(null, TimeSpan.Zero);
}
