// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Alerts.Domain;

/// <summary>
/// Top-level alerting feature configuration.
/// </summary>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public sealed class AlertOptions
{
    /// <summary>
    /// Configuration section used to bind alerting options.
    /// </summary>
    public const string SectionName = "Alerts";

    /// <summary>
    /// Enables alert processing workers.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Active edition used for feature gating.
    /// </summary>
    public AlertEdition Edition { get; init; } = AlertEdition.Pro;

    /// <summary>
    /// Evaluator worker settings.
    /// </summary>
    public AlertEvaluationOptions Evaluation { get; init; } = new();

    /// <summary>
    /// Dispatcher worker settings.
    /// </summary>
    public AlertDispatchOptions Dispatch { get; init; } = new();
}

/// <summary>
/// Settings for durable alert evaluation.
/// </summary>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public sealed class AlertEvaluationOptions
{
    /// <summary>
    /// Worker name used for checkpoint persistence.
    /// </summary>
    [Required]
    public string WorkerName { get; init; } = "evaluator";

    /// <summary>
    /// Maximum number of durable changes processed per batch.
    /// </summary>
    [Range(1, 5000)]
    public int ChangeBatchSize { get; init; } = 100;

    /// <summary>
    /// Interval for dwell sweep processing.
    /// </summary>
    public TimeSpan DwellSweepInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Interval to wait when no changes are available.
    /// </summary>
    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Lease duration for leader-election heartbeats.
    /// </summary>
    public TimeSpan LeaderLeaseDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Leader election strategy identifier.
    /// </summary>
    [Required]
    public string LeaderElectionMode { get; init; } = "postgres-advisory-lock";
}

/// <summary>
/// Settings for alert outbox dispatch processing.
/// </summary>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public sealed class AlertDispatchOptions
{
    /// <summary>
    /// Maximum number of outbox jobs claimed per dispatch cycle.
    /// </summary>
    [Range(1, 5000)]
    public int ClaimBatchSize { get; init; } = 100;

    /// <summary>
    /// Base retry delay used by exponential backoff.
    /// </summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum retry delay cap for exponential backoff.
    /// </summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Optional default webhook URL used when a dispatch row does not provide a destination.
    /// </summary>
    public string? DefaultWebhookUrl { get; init; }

    /// <summary>
    /// Shared HMAC secret used to sign webhook alert deliveries.
    /// </summary>
    public string? DefaultWebhookSecret { get; init; }

    /// <summary>
    /// Delay when no dispatch work is available.
    /// </summary>
    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Digest delivery settings.
    /// </summary>
    public DigestAlertOptions Digest { get; init; } = new();
}

/// <summary>
/// Settings for batched digest alert delivery.
/// </summary>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public sealed class DigestAlertOptions
{
    /// <summary>
    /// Webhook URL for digest delivery. If null, digest items are dead-lettered.
    /// </summary>
    public string? WebhookUrl { get; init; }

    /// <summary>
    /// Shared HMAC secret used to sign digest webhook deliveries.
    /// </summary>
    public string? WebhookSecret { get; init; }

    /// <summary>
    /// Interval between digest flushes.
    /// </summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maximum events per digest batch.
    /// </summary>
    [Range(1, 10000)]
    public int MaxBatchSize { get; init; } = 50;
}
