// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// Ordered steps of a coordinated platform-upgrade release (Demo C). A coordinated release composes
/// the existing container-deploy rollout, the additive DB script migration, and the metadata-v2
/// activation into one ordered, gated, rollback-able operation. The steps run top-to-bottom; a fault
/// at any step triggers a single coordinated rollback that unwinds the completed steps in reverse.
/// </summary>
public enum CoordinatedReleaseStep
{
    /// <summary>
    /// Validate the whole coordinated package: container image reference resolvable, DB script
    /// reversible, and the metadata semantic diff compatible. No side effects.
    /// </summary>
    Preflight,

    /// <summary>
    /// Capture any required backups before the data-affecting steps. A no-op for the additive
    /// reversible path, mirroring the metadata-release Backup stage.
    /// </summary>
    Backup,

    /// <summary>
    /// Roll out the new container image (canary/gated) via the existing deploy workflow. Gated at the
    /// container-swap boundary with explicit approval.
    /// </summary>
    ContainerRollout,

    /// <summary>
    /// Apply the additive DB schema script and activate the new metadata-v2 revision by driving the
    /// existing metadata-release lifecycle (its ScriptMigration → MetadataApply stages). Gated at the
    /// data-affecting boundary with explicit approval.
    /// </summary>
    MetadataAndSchema,

    /// <summary>
    /// Smoke/verify the upgraded platform end-to-end after all three artifacts are in place.
    /// </summary>
    Smoke,

    /// <summary>
    /// Promote the coordinated release to the target environment. Terminal success path.
    /// </summary>
    Promote,

    /// <summary>
    /// The coordinated release completed successfully.
    /// </summary>
    Complete,

    /// <summary>
    /// The coordinated release failed and a coordinated rollback is unwinding completed steps.
    /// </summary>
    RollingBack,

    /// <summary>
    /// The coordinated release failed before completion.
    /// </summary>
    Failed
}

/// <summary>
/// Lifecycle status of a single step inside a coordinated release.
/// </summary>
public enum CoordinatedReleaseStepStatus
{
    /// <summary>
    /// The step has not started yet.
    /// </summary>
    Pending,

    /// <summary>
    /// The step is waiting for explicit operator approval at a gate boundary.
    /// </summary>
    AwaitingApproval,

    /// <summary>
    /// The step is executing.
    /// </summary>
    Running,

    /// <summary>
    /// The step completed successfully.
    /// </summary>
    Succeeded,

    /// <summary>
    /// The step failed.
    /// </summary>
    Failed,

    /// <summary>
    /// The step was unwound by the coordinated rollback.
    /// </summary>
    RolledBack
}

/// <summary>
/// One artifact kind carried by a coordinated release proposal so the console can render the three
/// artifact kinds side by side (container image vs current, DB/schema change, metadata semantic diff).
/// </summary>
public enum CoordinatedReleaseArtifactKind
{
    /// <summary>
    /// A container image rollout artifact.
    /// </summary>
    ContainerImage,

    /// <summary>
    /// An additive DB/schema change artifact.
    /// </summary>
    DatabaseSchema,

    /// <summary>
    /// A metadata semantic-diff artifact.
    /// </summary>
    Metadata
}

/// <summary>
/// Container-image step desired state for a coordinated release. Resolved into a
/// <see cref="DeployOperationSpec"/> when the rollout step runs through the existing deploy workflow.
/// </summary>
public sealed record CoordinatedReleaseContainerSpec
{
    /// <summary>
    /// Stable deploy target identifier resolved through the deploy target registry.
    /// </summary>
    public required string TargetId { get; init; }

    /// <summary>
    /// Currently observed container image/revision before the upgrade.
    /// </summary>
    public string? CurrentImage { get; init; }

    /// <summary>
    /// Desired container image/revision the rollout converges to.
    /// </summary>
    public required string DesiredImage { get; init; }

    /// <summary>
    /// Whether the container-swap boundary requires explicit operator approval before the rollout
    /// step submits. Mirrors <see cref="MetadataRollbackPlan.RequiresExplicitApproval"/> gating.
    /// </summary>
    public bool RequiresExplicitApproval { get; init; } = true;
}

/// <summary>
/// Coordinated-release-specific context embedded in a durable workflow operation. Carries the three
/// artifact specs (container, DB-script + metadata) plus the ordered step ledger and the linked child
/// operation ids the conductor created for each composed lifecycle.
/// </summary>
public sealed record CoordinatedReleaseContext
{
    /// <summary>
    /// Release package identifier this coordinated upgrade advances. Shared with the metadata-release
    /// package so the console can correlate the coordinated op with the semantic diff.
    /// </summary>
    public required string PackageId { get; init; }

    /// <summary>
    /// Target environment for the coordinated upgrade.
    /// </summary>
    public required string TargetEnvironment { get; init; }

    /// <summary>
    /// Container-image rollout desired state.
    /// </summary>
    public required CoordinatedReleaseContainerSpec Container { get; init; }

    /// <summary>
    /// The additive DB-script + metadata-v2 activation desired state. Reuses the metadata-release
    /// execution plan unchanged so the DB ScriptMigration and metadata activation ride the existing
    /// lifecycle rather than a parallel implementation.
    /// </summary>
    public required MetadataReleaseExecutionPlan MetadataPlan { get; init; }

    /// <summary>
    /// Current coordinated step.
    /// </summary>
    public CoordinatedReleaseStep CurrentStep { get; init; } = CoordinatedReleaseStep.Preflight;

    /// <summary>
    /// Ordered ledger of step states, appended/updated as the conductor advances. Drives the console's
    /// ordered step timeline and per-step gate/approve surface.
    /// </summary>
    public IReadOnlyList<CoordinatedReleaseStepState> Steps { get; init; } = Array.Empty<CoordinatedReleaseStepState>();

    /// <summary>
    /// Child deploy operation id created for the container rollout step, when started.
    /// </summary>
    public string? ContainerOperationId { get; init; }

    /// <summary>
    /// Child metadata-release operation id created for the DB-script + metadata step, when started.
    /// </summary>
    public string? MetadataOperationId { get; init; }

    /// <summary>
    /// Whether the operator has approved the container-swap gate.
    /// </summary>
    public bool ContainerGateApproved { get; init; }

    /// <summary>
    /// Whether the operator has approved the data-affecting (DB/metadata) gate.
    /// </summary>
    public bool DataGateApproved { get; init; }

    /// <summary>
    /// Non-blocking advisory messages for the coordinated op.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Blocking reasons that currently prevent the coordinated op from advancing.
    /// </summary>
    public IReadOnlyList<string> Blockers { get; init; } = Array.Empty<string>();
}

/// <summary>
/// State of a single coordinated-release step in the durable ledger.
/// </summary>
public sealed record CoordinatedReleaseStepState
{
    /// <summary>
    /// The step this state describes.
    /// </summary>
    public required CoordinatedReleaseStep Step { get; init; }

    /// <summary>
    /// Current step status.
    /// </summary>
    public required CoordinatedReleaseStepStatus Status { get; init; }

    /// <summary>
    /// Whether the step is a gate that requires explicit operator approval before it runs.
    /// </summary>
    public bool RequiresApproval { get; init; }

    /// <summary>
    /// Whether the step mutates data or swaps the running container, so the coordinated rollback must
    /// unwind it. Used to order the reverse unwind and decide which child rollbacks to invoke.
    /// </summary>
    public bool IsReversible { get; init; }

    /// <summary>
    /// Operator-facing detail for the step.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Time the step last changed state.
    /// </summary>
    public DateTimeOffset At { get; init; }
}
