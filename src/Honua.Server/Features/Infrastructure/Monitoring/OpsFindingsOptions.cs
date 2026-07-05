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
}
