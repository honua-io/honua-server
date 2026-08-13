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
    /// <remarks>
    /// This property deliberately uses a setter because the source-generated configuration binder cannot
    /// assign init-only properties when binding an existing options instance.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Optional downward-only cap on the license-derived alert edition (#2998). When null
    /// (the default), the allowed alert triggers and delivery channels derive solely from the
    /// active license entitlements (<c>alerts.*</c>/<c>channels.*</c> keys). When set, the
    /// effective tier is the minimum of the license-derived tier and this value — the cap can
    /// only restrict features below what the license grants; it never unlocks features the
    /// license does not include.
    /// </summary>
    /// <remarks>
    /// Declared <c>set</c> rather than <c>init</c> deliberately: the configuration binding
    /// source generator (<c>EnableConfigurationBindingGenerator</c>) does not assign init-only
    /// properties when binding an existing options instance, so an init-only cap would silently
    /// stay null no matter what an operator configured. Covered by AlertOptionsBindingTests.
    /// </remarks>
    public AlertEdition? Edition { get; set; }

    /// <summary>
    /// Evaluator worker settings.
    /// </summary>
    public AlertEvaluationOptions Evaluation { get; set; } = new();

    /// <summary>
    /// Dispatcher worker settings.
    /// </summary>
    public AlertDispatchOptions Dispatch { get; set; } = new();

    /// <summary>
    /// Operations-notification settings (deploy/job terminal events delivered through
    /// the shared alert delivery outbox). Disabled by default.
    /// </summary>
    public AlertOpsOptions Ops { get; set; } = new();
}

/// <summary>
/// Settings for operations notifications: deploy-workflow and job terminal events
/// composed into ops alert events and delivered through the shared alert outbox.
/// </summary>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public sealed class AlertOpsOptions
{
    /// <summary>
    /// Enables ops notifications. Disabled by default; enabling requires the alert
    /// pipeline to also be enabled (<see cref="AlertOptions.Enabled"/>) for delivery.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Delivery channels (canonical channel names, e.g. <c>webhook</c>, <c>slack</c>)
    /// ops notifications are dispatched to. Channels disallowed by the active edition
    /// are dropped at composition time.
    /// </summary>
    public IReadOnlyList<string> Channels { get; set; } = [];

    /// <summary>
    /// Minimum severity delivered. Events below this severity are dropped before
    /// enqueue. Defaults to <see cref="AlertSeverity.Info"/> (deliver everything).
    /// </summary>
    public AlertSeverity MinSeverity { get; set; } = AlertSeverity.Info;
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
    public string WorkerName { get; set; } = "evaluator";

    /// <summary>
    /// Maximum number of durable changes processed per batch.
    /// </summary>
    [Range(1, 5000)]
    public int ChangeBatchSize { get; set; } = 100;

    /// <summary>
    /// Interval for dwell sweep processing.
    /// </summary>
    public TimeSpan DwellSweepInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Interval to wait when no changes are available.
    /// </summary>
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Lease duration for leader-election heartbeats.
    /// </summary>
    public TimeSpan LeaderLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Leader election strategy identifier.
    /// </summary>
    [Required]
    public string LeaderElectionMode { get; set; } = "postgres-advisory-lock";

    /// <summary>
    /// Age at or above which the evaluation-loop health check treats the current leader's
    /// heartbeat (its most recent productive pass) as stale and reports the leader hung.
    /// The evaluator loops at least once per <see cref="IdleDelay"/> while it holds
    /// leadership, so a heartbeat older than this threshold means the leader is wedged inside
    /// a pass rather than idle. Only meaningful on the node that currently holds leadership.
    /// </summary>
    public TimeSpan HeartbeatStalenessThreshold { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Duration for which every leadership-acquisition attempt must have been *failing*
    /// (the coordinator errored rather than cleanly observing another holder) before the
    /// evaluation health check fires the "no-leader" fault. This is the fleet-wide stall
    /// signal: when no node can acquire the advisory lock (e.g. connection-pool exhaustion),
    /// evaluation halts everywhere with no leader, so each node reports it after the threshold.
    /// A healthy follower (a clean "someone else leads" result) never trips this.
    /// </summary>
    public TimeSpan NoLeaderThreshold { get; set; } = TimeSpan.FromMinutes(2);
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
    public int ClaimBatchSize { get; set; } = 100;

    /// <summary>
    /// Base retry delay used by exponential backoff.
    /// </summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum retry delay cap for exponential backoff.
    /// </summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Optional default webhook URL used when a dispatch row does not provide a destination.
    /// </summary>
    public string? DefaultWebhookUrl { get; set; }

    /// <summary>
    /// Shared HMAC secret used to sign webhook alert deliveries.
    /// </summary>
    public string? DefaultWebhookSecret { get; set; }

    /// <summary>
    /// Delay when no dispatch work is available.
    /// </summary>
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Maximum outbound notifications delivered per channel per rolling minute,
    /// enforced <b>per replica</b>. When a channel exceeds this cap, further claimed
    /// dispatches for that channel are rescheduled (not dead-lettered) until the
    /// window frees. Set to <c>0</c> to disable the cap. Defaults to a generous
    /// 120/minute/channel.
    /// </summary>
    /// <remarks>
    /// The cap is enforced by a process-local, in-memory sliding window. The alert
    /// dispatch worker is deliberately multi-consumer (every replica claims work via
    /// <c>FOR UPDATE SKIP LOCKED</c> for throughput) and is <b>not</b> leader-elected,
    /// so this cap is best-effort per replica: the effective cluster-wide ceiling is
    /// approximately <c>value × replicaCount</c>. Size the value against a single
    /// replica's budget and rely on downstream provider rate limits for hard caps.
    /// </remarks>
    [Range(0, 1_000_000)]
    public int MaxNotificationsPerMinutePerChannel { get; set; } = 120;

    /// <summary>
    /// Backlog size (pending + retriable rows) at or above which the dispatch-backlog
    /// health check reports Degraded.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int DegradedBacklogThreshold { get; set; } = 1_000;

    /// <summary>
    /// Dead-lettered row count at or above which the dispatch-backlog health check
    /// reports Unhealthy.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int UnhealthyDeadLetterThreshold { get; set; } = 1;

    /// <summary>
    /// Minimum interval between dispatch-backlog recomputations. The backlog count is
    /// a full aggregate over the outbox, so it is refreshed at most once per interval
    /// (and always after a non-empty claim batch) rather than on every idle poll.
    /// </summary>
    public TimeSpan BacklogRefreshInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Age at or above which the dispatch-backlog health check treats the dispatcher's
    /// most recent poll heartbeat (<c>LastPollAt</c>) as stale and reports the dispatcher
    /// hung. The dispatcher records a heartbeat every pass (at least once per
    /// <see cref="IdleDelay"/>), so a heartbeat older than this threshold means the loop is
    /// wedged inside a pass while still reporting "running" — the failure mode a plain
    /// running-flag check misses. Must comfortably exceed <see cref="IdleDelay"/> and
    /// <see cref="BacklogRefreshInterval"/> so a merely-idle dispatcher is never flagged.
    /// </summary>
    public TimeSpan HeartbeatStalenessThreshold { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Retention window for delivered (status = delivered) dispatch rows. A periodic
    /// sweep purges delivered rows older than this so the outbox does not grow
    /// unbounded and backlog counts stay cheap. Set to <see cref="TimeSpan.Zero"/> or
    /// negative to disable purging.
    /// </summary>
    public TimeSpan DeliveredRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Minimum interval between delivered-row retention sweeps.
    /// </summary>
    public TimeSpan RetentionSweepInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum delivered rows deleted per retention sweep pass (bounded delete to avoid
    /// long-running transactions).
    /// </summary>
    [Range(1, 1_000_000)]
    public int RetentionBatchSize { get; set; } = 1_000;

    /// <summary>
    /// Number of consecutive dead-lettered deliveries on a single channel that trips the
    /// per-channel delivery circuit breaker. While the breaker is open, further claimed
    /// dispatches for that channel are deferred (rescheduled, retry budget untouched) and
    /// newly composed ops notifications skip the channel — so a permanently-failing channel
    /// (for example a dead ops webhook) produces bounded dead-letter volume instead of one
    /// dead-letter per recurring event. A single half-open probe is admitted after
    /// <see cref="CircuitBreakerCooldown"/>; a success closes the breaker. Set to <c>0</c> to
    /// disable circuit breaking. Defaults to 5.
    /// </summary>
    [Range(0, 1_000_000)]
    public int CircuitBreakerThreshold { get; set; } = 5;

    /// <summary>
    /// How long a tripped per-channel delivery circuit breaker stays open before admitting a
    /// single half-open probe. Defaults to 5 minutes.
    /// </summary>
    public TimeSpan CircuitBreakerCooldown { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Digest delivery settings.
    /// </summary>
    public DigestAlertOptions Digest { get; set; } = new();
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
    public string? WebhookUrl { get; set; }

    /// <summary>
    /// Shared HMAC secret used to sign digest webhook deliveries.
    /// </summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// Interval between digest flushes.
    /// </summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maximum events per digest batch.
    /// </summary>
    [Range(1, 10000)]
    public int MaxBatchSize { get; set; } = 50;
}
