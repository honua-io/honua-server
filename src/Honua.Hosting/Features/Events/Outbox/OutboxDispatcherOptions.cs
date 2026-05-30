// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Events.Outbox;

/// <summary>
/// Runtime configuration for the feature-change outbox dispatcher.
/// </summary>
public sealed class OutboxDispatcherOptions
{
    public const string SectionName = "FeatureChangeEvents:Outbox";

    /// <summary>Number of rows the dispatcher claims per pass.</summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>Idle poll delay when the previous pass yielded no rows.</summary>
    public int IdlePollIntervalMs { get; set; } = 1_000;

    /// <summary>Claim lease TTL — sets how long another node will wait before reclaiming an orphaned row.</summary>
    public int ClaimTtlSeconds { get; set; } = 30;

    /// <summary>How often the recovery loop resets expired claim leases.</summary>
    public int RecoveryIntervalSeconds { get; set; } = 30;

    /// <summary>Maximum dispatch attempts before a row is dead-lettered.</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>Backlog threshold above which the health check transitions to Degraded.</summary>
    public int DegradedBacklogThreshold { get; set; } = 1_000;

    /// <summary>Number of dead-lettered rows that pushes the health check to Unhealthy.</summary>
    public int UnhealthyDeadLetterThreshold { get; set; } = 1;
}
