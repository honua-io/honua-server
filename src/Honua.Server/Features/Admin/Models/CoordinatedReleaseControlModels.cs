// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Request to create a coordinated platform-upgrade release (Demo C): a new container image, an
/// additive DB/schema change, and a metadata-v2 activation submitted as one ordered, gated,
/// rollback-able operation.
/// </summary>
public sealed record CreateCoordinatedReleaseOperationRequest
{
    /// <summary>
    /// Release package identifier shared with the metadata semantic-diff package.
    /// </summary>
    public string? PackageId { get; init; }

    /// <summary>
    /// Target environment for the coordinated upgrade.
    /// </summary>
    public string? TargetEnvironment { get; init; }

    /// <summary>
    /// Deploy target id for the container rollout step.
    /// </summary>
    public string? ContainerTargetId { get; init; }

    /// <summary>
    /// Desired container image/revision the rollout converges to.
    /// </summary>
    public string? DesiredImage { get; init; }

    /// <summary>
    /// Currently observed container image/revision before the upgrade.
    /// </summary>
    public string? CurrentImage { get; init; }

    /// <summary>
    /// Semantic identifier of the resource/layer evolved by the additive metadata/schema change.
    /// </summary>
    public string? ResourceSemanticId { get; init; }

    /// <summary>
    /// Name of the newly added nullable field.
    /// </summary>
    public string? NewFieldName { get; init; }

    /// <summary>
    /// Canonical type for the new field (for example <c>String</c> or <c>Integer</c>).
    /// </summary>
    public string? NewFieldType { get; init; }

    /// <summary>
    /// Optional ETL/data-populate workload id dispatched after the schema change.
    /// </summary>
    public string? DataPopulateWorkloadId { get; init; }

    /// <summary>
    /// Optional explicit schema script id; defaults to <c>add-{newFieldName}</c>.
    /// </summary>
    public string? ScriptId { get; init; }

    /// <summary>
    /// Free-form operator reason/change note.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Idempotency key for safe submission replay.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// Upstream correlation id.
    /// </summary>
    public string? CorrelationId { get; init; }
}

/// <summary>
/// Request body for approving a coordinated-release gate or requesting its rollback.
/// </summary>
public sealed record CoordinatedReleaseActionRequest
{
    /// <summary>
    /// Operator reason/change note for the action.
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// One artifact carried by the coordinated release proposal (container image, DB/schema change, or
/// metadata semantic diff) so the console can render the three kinds side by side.
/// </summary>
public sealed record CoordinatedReleaseArtifactResponse
{
    /// <summary>
    /// Artifact kind: <c>ContainerImage</c>, <c>DatabaseSchema</c>, or <c>Metadata</c>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Operator-facing artifact title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Desired state for the artifact (image ref, schema change, or activated revision).
    /// </summary>
    public required string Desired { get; init; }

    /// <summary>
    /// Current state for the artifact, when known.
    /// </summary>
    public string? Current { get; init; }
}

/// <summary>
/// One step in the coordinated ordered step timeline, including its gate and rollback status.
/// </summary>
public sealed record CoordinatedReleaseStepResponse
{
    /// <summary>
    /// Step name.
    /// </summary>
    public required string Step { get; init; }

    /// <summary>
    /// Step status.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Whether this step is an approval gate.
    /// </summary>
    public bool RequiresApproval { get; init; }

    /// <summary>
    /// Whether this step is reversible and is unwound by the coordinated rollback.
    /// </summary>
    public bool IsReversible { get; init; }

    /// <summary>
    /// Operator-facing detail.
    /// </summary>
    public string? Detail { get; init; }
}

/// <summary>
/// Full coordinated-release operation view consumed by the console: the three artifact kinds, the
/// ordered step timeline with per-step gate/approve status, and the rollback readiness for the op.
/// </summary>
public sealed record CoordinatedReleaseOperationResponse
{
    /// <summary>
    /// Coordinated workflow operation id.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Release package id.
    /// </summary>
    public required string PackageId { get; init; }

    /// <summary>
    /// Coordinated workflow status.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Current coordinated step.
    /// </summary>
    public required string CurrentStep { get; init; }

    /// <summary>
    /// Target environment.
    /// </summary>
    public required string TargetEnvironment { get; init; }

    /// <summary>
    /// Operator-facing current phase.
    /// </summary>
    public string? CurrentPhase { get; init; }

    /// <summary>
    /// The three coordinated artifact kinds.
    /// </summary>
    public IReadOnlyList<CoordinatedReleaseArtifactResponse> Artifacts { get; init; } = [];

    /// <summary>
    /// Ordered step timeline.
    /// </summary>
    public IReadOnlyList<CoordinatedReleaseStepResponse> Steps { get; init; } = [];

    /// <summary>
    /// Whether the container-swap gate is approved.
    /// </summary>
    public bool ContainerGateApproved { get; init; }

    /// <summary>
    /// Whether the data-affecting (DB/metadata) gate is approved.
    /// </summary>
    public bool DataGateApproved { get; init; }

    /// <summary>
    /// Child container deploy operation id, when started.
    /// </summary>
    public string? ContainerOperationId { get; init; }

    /// <summary>
    /// Child metadata-release operation id, when started.
    /// </summary>
    public string? MetadataOperationId { get; init; }

    /// <summary>
    /// Whether the coordinated op can be rolled back: at least one reversible step has run.
    /// </summary>
    public bool RollbackReady { get; init; }

    /// <summary>
    /// Blocking reasons.
    /// </summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];

    /// <summary>
    /// Warnings.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Error message for failed coordinated ops.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creation time.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Last update time.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Completion time when terminal.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }
}
