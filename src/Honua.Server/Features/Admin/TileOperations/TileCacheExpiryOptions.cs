// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.TileOperations;

/// <summary>
/// Configuration for the scheduled tile-cache expiry/invalidation service (#1837). Operators add
/// targets under the <c>TileCacheExpiry</c> configuration section; the hosted service periodically
/// dispatches an <c>invalidate</c> tile operation for each target so cached tiles for a tileset are
/// refreshed on a cadence (the time-based complement to the per-tileset TTL shipped in #1794).
/// </summary>
internal sealed class TileCacheExpiryOptions
{
    /// <summary>The configuration section name that binds to these options.</summary>
    public const string SectionName = "TileCacheExpiry";

    /// <summary>
    /// Whether scheduled expiry is enabled. Defaults to <see langword="false" /> so the baseline
    /// deployment performs no scheduled invalidation until an operator opts in.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How often the service sweeps the configured targets, in seconds. Values below the minimum
    /// sweep interval are clamped up to protect the serving pod. Defaults to one hour.
    /// </summary>
    public int IntervalSeconds { get; set; } = 3600;

    /// <summary>The tilesets to invalidate on each sweep.</summary>
    public List<TileCacheExpiryTarget> Targets { get; set; } = [];
}

/// <summary>
/// One scheduled expiry target: the tileset whose cached tiles should be invalidated on each
/// sweep. A target must identify at least a service or a layer (mirrors the invalidate operation).
/// </summary>
internal sealed class TileCacheExpiryTarget
{
    /// <summary>The logical service identity, or <see langword="null" /> to target by layer only.</summary>
    public string? ServiceId { get; set; }

    /// <summary>The layer identity, or <see langword="null" /> to target the whole service.</summary>
    public int? LayerId { get; set; }

    /// <summary>The tile matrix set identity (informational; invalidation is tileset-wide).</summary>
    public string? TileMatrixSetId { get; set; }
}
