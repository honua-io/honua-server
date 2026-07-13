// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Thresholds that drive the deterministic ops-findings rule set (<c>Observability:OpsFindings</c>).
/// Defaults are conservative so the engine only surfaces findings for genuinely notable conditions.
/// </summary>
public sealed class OpsFindingsOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Observability:OpsFindings";

    /// <summary>
    /// Gets or sets the alert-dispatch pending backlog count at or above which the
    /// <c>alert-dispatch-backlog</c> rule raises a warning finding. Default is 250.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long AlertDispatchPendingBacklogThreshold { get; set; } = 250;

    /// <summary>
    /// Gets or sets the alert-dispatch dead-letter count at or above which the
    /// <c>alert-dispatch-backlog</c> rule escalates to a critical finding. Default is 1
    /// (any dead-lettered notification is worth operator attention).
    /// </summary>
    [Range(1, long.MaxValue)]
    public long AlertDispatchDeadLetterThreshold { get; set; } = 1;

    /// <summary>
    /// Gets or sets the total active GP queue depth (queued + provisioning + running) at or above
    /// which the <c>gp-queue-depth</c> rule raises a finding. Default is 200.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int GpQueueDepthThreshold { get; set; } = 200;

    /// <summary>
    /// Gets or sets the database connection-pool utilization ratio (0.0-1.0) at or above which the
    /// <c>db-bounded-admission-pressure</c> rule raises a finding. Default is 0.9 (90%).
    /// </summary>
    [Range(0.01, 1.0)]
    public double DbPoolUtilizationThreshold { get; set; } = 0.9;

    /// <summary>
    /// Gets or sets the connection-acquisition timeout count at or above which the
    /// <c>db-bounded-admission-pressure</c> rule raises a finding. Default is 5.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long DbConnectionAcquisitionTimeoutThreshold { get; set; } = 5;

    /// <summary>
    /// Gets or sets the fraction (0.0-1.0, exclusive of the current admission target) by which a
    /// proposed <c>db.tune_bounded_admission</c> action lowers the current admission target when
    /// database connection-pool pressure is elevated. Default is 0.25 (a 25% reduction, floored at
    /// the gate's configured minimum).
    /// </summary>
    [Range(0.01, 0.9)]
    public double DbBoundedAdmissionTuneDownFraction { get; set; } = 0.25;

    /// <summary>
    /// Gets or sets the per-protocol 95th-percentile serving-latency threshold (milliseconds) at or
    /// above which the <c>serving-latency-slo-breach</c> rule raises a finding. Default is 2000ms.
    /// </summary>
    [Range(1, double.MaxValue)]
    public double ServingLatencyP95ThresholdMs { get; set; } = 2000;

    /// <summary>
    /// Gets or sets the per-protocol 99th-percentile serving-latency threshold (milliseconds) at or
    /// above which the <c>serving-latency-slo-breach</c> rule raises a finding. The p99 tail is a
    /// stricter tail-latency guard than p95: a breach here catches a small fraction of very slow
    /// requests (burn-rate) that p95 can mask. Default is 4000ms.
    /// </summary>
    [Range(1, double.MaxValue)]
    public double ServingLatencyP99ThresholdMs { get; set; } = 4000;

    /// <summary>
    /// Gets or sets the per-protocol server-error rate (0.0-1.0) at or above which the
    /// <c>serving-latency-slo-breach</c> rule raises a finding. Default is 0.05 (5%).
    /// </summary>
    [Range(0.0001, 1.0)]
    public double ServingErrorRateThreshold { get; set; } = 0.05;

    /// <summary>
    /// Gets or sets the lookback window (minutes) the <c>serving-latency-slo-breach</c> rule reads
    /// from the persisted ops-health rollup store's 1-minute tier (#2553). Default is 5 minutes.
    /// </summary>
    [Range(1, 1440)]
    public double ServingLatencyLookbackMinutes { get; set; } = 5;
}
