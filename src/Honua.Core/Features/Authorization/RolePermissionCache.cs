// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization;

/// <summary>
/// Process-wide, short-TTL cache for resolved role-permission lookups (#1593).
/// Registered as a singleton so the per-request scoped <c>IRoleStore</c>
/// (e.g. the Postgres-backed store) can share resolved grants across requests
/// instead of issuing a database round-trip on every authorization decision.
/// </summary>
/// <remarks>
/// <para>
/// Entries are keyed by the normalized role-name set (see
/// <see cref="CreateKey(System.Collections.Generic.IReadOnlyList{string})"/>) because
/// effective permission grants depend only on which roles are held, never on the
/// user identity — so distinct users sharing the same role set share one entry.
/// </para>
/// <para>
/// <b>Staleness contract:</b> same-node role/grant mutations invalidate the
/// cache immediately via <see cref="InvalidateAll"/> (wired by
/// <see cref="CachingRoleStore"/>), so local changes are visible on the next
/// authorization check. On multi-node deployments, mutations performed on
/// another node become visible once the entry's TTL elapses; authorization data
/// therefore tolerates at most <see cref="Ttl"/> of cross-node staleness
/// (default 30 seconds).
/// </para>
/// <para>
/// The cache is thread-safe, bounded by <see cref="MaxEntries"/> (expired
/// entries are swept first; if the cache is still full it is cleared, which is
/// safe because every miss simply falls through to the authoritative store),
/// and allocation-light on the hit path (a single dictionary lookup).
/// </para>
/// </remarks>
public sealed class RolePermissionCache
{
    /// <summary>Default time-to-live applied when none is supplied.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);

    /// <summary>Default upper bound on the number of cached role-set entries.</summary>
    public const int DefaultMaxEntries = 1024;

    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="RolePermissionCache"/> class.
    /// </summary>
    /// <param name="ttl">Time-to-live for cached entries; defaults to
    /// <see cref="DefaultTtl"/> (30 seconds). Must be positive. This bounds how
    /// long a role/grant change made on another node can remain unobserved.</param>
    /// <param name="maxEntries">Upper bound on cached role-set entries; defaults
    /// to <see cref="DefaultMaxEntries"/>. Must be positive.</param>
    /// <param name="timeProvider">Optional time source (testing seam); defaults
    /// to <see cref="TimeProvider.System"/>.</param>
    public RolePermissionCache(TimeSpan? ttl = null, int maxEntries = DefaultMaxEntries, TimeProvider? timeProvider = null)
    {
        var effectiveTtl = ttl ?? DefaultTtl;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(effectiveTtl, TimeSpan.Zero, nameof(ttl));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxEntries, 0, nameof(maxEntries));

        Ttl = effectiveTtl;
        MaxEntries = maxEntries;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the time-to-live applied to cached entries. This is the maximum
    /// cross-node staleness window for authorization data served from cache.
    /// </summary>
    public TimeSpan Ttl { get; }

    /// <summary>
    /// Gets the upper bound on the number of cached role-set entries.
    /// </summary>
    public int MaxEntries { get; }

    /// <summary>
    /// Gets the current number of cached entries (including not-yet-swept
    /// expired entries). Intended for diagnostics and tests.
    /// </summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Builds the canonical cache key for a role-name set: names are trimmed,
    /// lower-cased invariantly, de-duplicated, and sorted so equivalent role
    /// sets (any order, any casing) share one cache entry. Returns
    /// <see cref="string.Empty"/> when no usable role names are present, which
    /// callers must treat as "do not cache".
    /// </summary>
    /// <param name="roles">The role names held by the principal.</param>
    /// <returns>A canonical key, or <see cref="string.Empty"/> when the set is empty.</returns>
    public static string CreateKey(IReadOnlyList<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        if (roles.Count == 0)
        {
            return string.Empty;
        }

        var normalized = new List<string>(roles.Count);
        foreach (var role in roles)
        {
            if (!string.IsNullOrWhiteSpace(role))
            {
                normalized.Add(role.Trim().ToLowerInvariant());
            }
        }

        if (normalized.Count == 0)
        {
            return string.Empty;
        }

        normalized.Sort(StringComparer.Ordinal);

        // De-duplicate adjacent entries post-sort without an extra set allocation.
        var builder = new System.Text.StringBuilder(normalized.Count * 12);
        string? previous = null;
        foreach (var name in normalized)
        {
            if (string.Equals(name, previous, StringComparison.Ordinal))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(name);
            previous = name;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Attempts to read the cached permission grants for a role-set key.
    /// Expired entries are removed lazily and reported as misses.
    /// </summary>
    /// <param name="key">The key produced by <see cref="CreateKey(System.Collections.Generic.IReadOnlyList{string})"/>.</param>
    /// <param name="permissions">The cached grants on a hit. Treat as immutable —
    /// the list is shared across concurrent requests.</param>
    /// <returns><see langword="true"/> on a fresh cache hit; otherwise <see langword="false"/>.</returns>
    public bool TryGet(string key, out IReadOnlyList<PermissionGrant> permissions)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length != 0 && _entries.TryGetValue(key, out var entry))
        {
            if (_timeProvider.GetUtcNow().UtcTicks <= entry.ExpiresAtUtcTicks)
            {
                permissions = entry.Permissions;
                return true;
            }

            // Expired: remove only this exact entry so a concurrent fresh Set is preserved.
            _entries.TryRemove(new KeyValuePair<string, CacheEntry>(key, entry));
        }

        permissions = [];
        return false;
    }

    /// <summary>
    /// Caches the resolved permission grants for a role-set key with the
    /// configured TTL. No-ops for the empty key. When the cache is full,
    /// expired entries are swept first; if still full, all entries are dropped
    /// (subsequent misses re-resolve from the authoritative store).
    /// </summary>
    /// <param name="key">The key produced by <see cref="CreateKey(System.Collections.Generic.IReadOnlyList{string})"/>.</param>
    /// <param name="permissions">The resolved grants to cache. Callers must not
    /// mutate the list after handing it to the cache.</param>
    public void Set(string key, IReadOnlyList<PermissionGrant> permissions)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(permissions);

        if (key.Length == 0)
        {
            return;
        }

        if (_entries.Count >= MaxEntries && !_entries.ContainsKey(key))
        {
            SweepExpired();
            if (_entries.Count >= MaxEntries)
            {
                _entries.Clear();
            }
        }

        var expiresAt = _timeProvider.GetUtcNow().UtcTicks + Ttl.Ticks;
        _entries[key] = new CacheEntry(permissions, expiresAt);
    }

    /// <summary>
    /// Drops every cached entry. Invoked on role/grant mutation paths so
    /// same-node changes are visible on the next authorization check.
    /// </summary>
    public void InvalidateAll() => _entries.Clear();

    private void SweepExpired()
    {
        var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
        foreach (var pair in _entries)
        {
            if (nowTicks > pair.Value.ExpiresAtUtcTicks)
            {
                _entries.TryRemove(pair);
            }
        }
    }

    private readonly record struct CacheEntry(IReadOnlyList<PermissionGrant> Permissions, long ExpiresAtUtcTicks);
}
