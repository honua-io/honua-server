// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Options for the persisted ops-health rollup store and its per-replica sampler (<c>Observability:OpsHealthRollup</c>, #2553).
/// </summary>
public sealed class OpsHealthRollupOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Observability:OpsHealthRollup";

    /// <summary>
    /// Gets or sets a value indicating whether the background sampler runs. Default is <c>true</c>. The sampler
    /// is additionally a no-op when no rollup store is registered (non-Postgres providers).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the sampler flush interval in seconds. Default is 45s (within the ~30–60s target band).
    /// </summary>
    [Range(5, 3600)]
    public int FlushIntervalSeconds { get; set; } = 45;

    /// <summary>
    /// Gets or sets the interval, in seconds, between downsample-and-prune maintenance passes. Default is 300s.
    /// </summary>
    [Range(30, 86_400)]
    public int MaintenanceIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Gets or sets the recency window, in seconds, over which the live snapshot merges other replicas' most
    /// recent flushes. Default is 120s (two flush intervals of headroom).
    /// </summary>
    [Range(30, 3600)]
    public int LiveMergeRecencyWindowSeconds { get; set; } = 120;

    /// <summary>Gets or sets the 1-minute-tier retention in hours. Default is 24.</summary>
    [Range(1, 168)]
    public int OneMinuteRetentionHours { get; set; } = 24;

    /// <summary>Gets or sets the 5-minute-tier retention in days. Default is 14.</summary>
    [Range(1, 90)]
    public int FiveMinuteRetentionDays { get; set; } = 14;

    /// <summary>Gets or sets the hourly-tier retention in days. Default is 90.</summary>
    [Range(1, 730)]
    public int HourlyRetentionDays { get; set; } = 90;

    /// <summary>
    /// Gets or sets an explicit replica identifier. When blank, a stable per-process identifier
    /// (<c>{MachineName}-{ProcessId}</c>) is used, matching the server's other per-replica components.
    /// </summary>
    public string? ReplicaId { get; set; }

    /// <summary>Gets the resolved retention policy.</summary>
    /// <returns>The retention policy derived from the configured caps.</returns>
    public OpsHealthRollupRetentionPolicy ToRetentionPolicy() => new()
    {
        OneMinuteRetention = TimeSpan.FromHours(OneMinuteRetentionHours),
        FiveMinuteRetention = TimeSpan.FromDays(FiveMinuteRetentionDays),
        HourlyRetention = TimeSpan.FromDays(HourlyRetentionDays),
    };

    /// <summary>Resolves the effective replica identifier.</summary>
    /// <returns>The configured replica id, or the default per-process identifier.</returns>
    public string ResolveReplicaId()
        => string.IsNullOrWhiteSpace(ReplicaId)
            ? string.Create(CultureInfo.InvariantCulture, $"{Environment.MachineName}-{Environment.ProcessId}")
            : ReplicaId.Trim();
}
