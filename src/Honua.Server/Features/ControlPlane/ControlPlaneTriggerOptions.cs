// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.ControlPlane;

/// <summary>
/// How the control plane drives reconciliation for every operation family: execution jobs,
/// deploy workflows, and the staged metadata-release / coordinated-release lifecycles.
/// </summary>
internal enum ControlPlaneTriggerMode
{
    /// <summary>
    /// On-prem / portable default. The 5-second poll loops
    /// (<c>ExecutionJobReconcilerBackgroundService</c>, <c>DeployWorkflowReconcilerBackgroundService</c>,
    /// <c>MetadataReleaseReconcilerBackgroundService</c>, <c>CoordinatedReleaseReconcilerBackgroundService</c>)
    /// drive reconciliation exactly as they do today. Byte-for-byte unchanged.
    /// </summary>
    Poll,

    /// <summary>
    /// Cloud event-driven mode. The 5-second poll loops are disabled for every operation family;
    /// reconciliation is driven by the event handler (an AWS Lambda invoked from EventBridge —
    /// "Batch Job State Change" for execution jobs; ECS Task State Change / CodeDeploy
    /// DeploymentStateChange / Lambda-alias events for deploys; and custom staged self-continue
    /// signals for metadata/coordinated releases) plus the low-frequency backstop sweeps that
    /// self-heal dropped/missed events for both the execution-job store and the workflow store.
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
    /// Reconcile trigger mode for all operation families (execution jobs, deploys, and the staged
    /// metadata/coordinated releases). Defaults to <see cref="ControlPlaneTriggerMode.Poll"/> so an
    /// unconfigured on-prem deployment keeps the existing poll behavior.
    /// </summary>
    public ControlPlaneTriggerMode TriggerMode { get; set; } = ControlPlaneTriggerMode.Poll;

    /// <summary>
    /// How often the backstop sweeps run. Both the execution-job and the workflow-operation
    /// (deploy/metadata/coordinated) backstops ship in BOTH modes so a dropped or missed event —
    /// or a lost staged self-continue signal — self-heals; they are low-frequency on purpose. Works
    /// on-prem and can also be invoked by EventBridge Scheduler in the cloud.
    /// </summary>
    public TimeSpan BackstopInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How stale (by <c>UpdatedAt</c>) a non-terminal operation must be before the backstop
    /// reconciles it. Fresh operations that an event already advanced are skipped, so the backstop is
    /// a no-op in the common case and only re-drives operations that look stuck. For the staged
    /// releases this is also the per-stage advance interval when only the backstop is driving them.
    /// </summary>
    public TimeSpan StaleThreshold { get; set; } = TimeSpan.FromSeconds(90);
}
