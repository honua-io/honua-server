// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Alerts.Domain;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public sealed class AlertOptions
{
    public const string SectionName = "Alerts";

    // The setter exists because the source-generated configuration binder cannot assign an init-only property on an existing instance.
    public bool Enabled { get; set; }

    // The edition cap can only restrict what the license already grants; it never unlocks a higher tier.
    public AlertEdition? Edition { get; set; }

    public AlertEvaluationOptions Evaluation { get; set; } = new();

    public AlertDispatchOptions Dispatch { get; set; } = new();

    public AlertOpsOptions Ops { get; set; } = new();
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public sealed class AlertOpsOptions
{
    public bool Enabled { get; set; }

    public IReadOnlyList<string> Channels { get; set; } = [];

    public AlertSeverity MinSeverity { get; set; } = AlertSeverity.Info;
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public sealed class AlertEvaluationOptions
{
    [Required]
    public string WorkerName { get; set; } = "evaluator";

    [Range(1, 5000)]
    public int ChangeBatchSize { get; set; } = 100;

    public TimeSpan DwellSweepInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan LeaderLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    [Required]
    public string LeaderElectionMode { get; set; } = "postgres-advisory-lock";

    public TimeSpan HeartbeatStalenessThreshold { get; set; } = TimeSpan.FromMinutes(2);

    // The no-leader fault fires only after leadership acquisition itself has been failing, not when another instance cleanly holds the lease.
    public TimeSpan NoLeaderThreshold { get; set; } = TimeSpan.FromMinutes(2);
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public sealed class AlertDispatchOptions
{
    [Range(1, 5000)]
    public int ClaimBatchSize { get; set; } = 100;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    public string? DefaultWebhookUrl { get; set; }

    public string? DefaultWebhookSecret { get; set; }

    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromSeconds(2);

    // The cap is per replica; a dispatch over the cap is rescheduled and is not dead-lettered, and zero disables the cap.
    [Range(0, 1_000_000)]
    public int MaxNotificationsPerMinutePerChannel { get; set; } = 120;

    [Range(1, int.MaxValue)]
    public int DegradedBacklogThreshold { get; set; } = 1_000;

    [Range(1, int.MaxValue)]
    public int UnhealthyDeadLetterThreshold { get; set; } = 1;

    public TimeSpan BacklogRefreshInterval { get; set; } = TimeSpan.FromSeconds(15);

    // A heartbeat older than this threshold means the dispatcher loop is stuck inside a pass while still reporting that it is running.
    public TimeSpan HeartbeatStalenessThreshold { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan DeliveredRetention { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan RetentionSweepInterval { get; set; } = TimeSpan.FromMinutes(5);

    [Range(1, 1_000_000)]
    public int RetentionBatchSize { get; set; } = 1_000;

    // While the breaker is open, claimed dispatches are deferred without spending retry budget, so a dead channel cannot grow the dead-letter set without bound.
    [Range(0, 1_000_000)]
    public int CircuitBreakerThreshold { get; set; } = 5;

    public TimeSpan CircuitBreakerCooldown { get; set; } = TimeSpan.FromMinutes(5);

    public DigestAlertOptions Digest { get; set; } = new();
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public sealed class DigestAlertOptions
{
    public string? WebhookUrl { get; set; }

    public string? WebhookSecret { get; set; }

    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMinutes(15);

    [Range(1, 10000)]
    public int MaxBatchSize { get; set; } = 50;
}
