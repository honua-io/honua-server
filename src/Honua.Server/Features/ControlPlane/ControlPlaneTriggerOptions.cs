// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.ControlPlane;

/// <summary>
/// How the control plane drives execution-job reconciliation.
/// </summary>
internal enum ControlPlaneTriggerMode
{
    /// <summary>
    /// On-prem / portable default. The 5-second <c>ExecutionJobReconcilerBackgroundService</c>
    /// poll loop drives reconciliation exactly as it does today. Byte-for-byte unchanged.
    /// </summary>
    Poll,

    /// <summary>
    /// Cloud event-driven mode. The 5-second execution-job poll loop is disabled; reconciliation
    /// is driven by the event handler (an AWS Lambda invoked from an EventBridge "Batch Job State
    /// Change" event) plus the low-frequency backstop sweep that self-heals dropped/missed events.
    /// </summary>
    Event
}

/// <summary>
/// Configuration for control-plane reconcile triggering. Bound from the <c>ControlPlane</c>
/// configuration section; the trigger seam lets cloud deployments go event-driven while on-prem
/// stays poll-driven from one codebase (control-plane hybrid-trigger design, option C).
/// </summary>
internal sealed class ControlPlaneTriggerOptions
{
    /// <summary>
    /// Configuration section name. Reuses the existing <c>ControlPlane</c> section so the trigger
    /// keys sit alongside the catalogs (for example <c>ControlPlane:TriggerMode</c>).
    /// </summary>
    public const string SectionName = "ControlPlane";

    /// <summary>
    /// Execution-job trigger mode. Defaults to <see cref="ControlPlaneTriggerMode.Poll"/> so an
    /// unconfigured on-prem deployment keeps the existing poll behavior.
    /// </summary>
    public ControlPlaneTriggerMode TriggerMode { get; set; } = ControlPlaneTriggerMode.Poll;

    /// <summary>
    /// How often the backstop sweep runs. The backstop ships with Phase 1 in BOTH modes so a
    /// dropped or missed event self-heals; it is low-frequency on purpose. Works on-prem and can
    /// also be invoked by EventBridge Scheduler in the cloud.
    /// </summary>
    public TimeSpan BackstopInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How stale (by <c>UpdatedAt</c>) a non-terminal execution job must be before the backstop
    /// reconciles it. Fresh jobs that an event already advanced are skipped, so the backstop is a
    /// no-op in the common case and only re-drives jobs that look stuck.
    /// </summary>
    public TimeSpan StaleThreshold { get; set; } = TimeSpan.FromSeconds(90);
}
