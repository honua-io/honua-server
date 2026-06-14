// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Temporal.Domain;

/// <summary>
/// The disposition of a governed rollback target (slice 5 of honua-server#1166). Reports whether the
/// rollback can be executed automatically through the job runner or needs operator action.
/// </summary>
public enum TemporalRollbackState
{
    /// <summary>The rollback can run automatically as a forward corrective job.</summary>
    Supported = 0,

    /// <summary>The rollback is blocked by a validation or compatibility finding and cannot proceed.</summary>
    Blocked = 1,

    /// <summary>The rollback requires a compensating script outside the automatic corrective path.</summary>
    ScriptRequired = 2,

    /// <summary>The rollback requires a dedicated job (for example a large bulk corrective load).</summary>
    JobRequired = 3,

    /// <summary>The rollback requires operator-managed manual recovery.</summary>
    Manual = 4
}

/// <summary>
/// The severity of a rollback planning finding.
/// </summary>
public enum TemporalRollbackFindingSeverity
{
    /// <summary>Informational; does not block the rollback.</summary>
    Info = 0,

    /// <summary>Advisory; the rollback can proceed but the operator should review.</summary>
    Warning = 1,

    /// <summary>Blocking; the rollback cannot proceed until resolved.</summary>
    Error = 2
}

/// <summary>
/// A single validation or compatibility finding produced during rollback planning.
/// </summary>
/// <param name="Code">Stable machine-readable finding code.</param>
/// <param name="Severity">Finding severity.</param>
/// <param name="Message">Operator-facing description (safe for client display; no provider internals).</param>
public sealed record TemporalRollbackFinding(
    string Code,
    TemporalRollbackFindingSeverity Severity,
    string Message);

/// <summary>
/// The plan for a governed rollback to a target checkpoint (slice 5 of honua-server#1166). Reports the
/// rollback state, affected counts, validation and compatibility findings, and the approval requirement
/// before any execution can be requested. Planning never mutates data.
/// </summary>
/// <param name="ServiceId">Owning service id.</param>
/// <param name="LayerId">Service-local layer index.</param>
/// <param name="TargetCheckpoint">The resolved checkpoint the rollback would restore the layer to.</param>
/// <param name="CurrentGeneration">Current change-tracker generation at planning time.</param>
/// <param name="State">The rollback disposition.</param>
/// <param name="AffectedFeatureCount">Number of features the corrective operation would touch.</param>
/// <param name="ValidationFindings">Validation findings (data-level checks).</param>
/// <param name="CompatibilityFindings">Compatibility findings (schema/version-interop checks).</param>
/// <param name="RequiresApproval">True when the rollback requires explicit approval before execution.</param>
public sealed record TemporalRollbackPlan(
    string ServiceId,
    int LayerId,
    ResolvedTemporalCheckpoint TargetCheckpoint,
    long CurrentGeneration,
    TemporalRollbackState State,
    int AffectedFeatureCount,
    ImmutableArray<TemporalRollbackFinding> ValidationFindings,
    ImmutableArray<TemporalRollbackFinding> CompatibilityFindings,
    bool RequiresApproval);

/// <summary>
/// A normalized rollback planning request.
/// </summary>
/// <param name="TargetCheckpoint">The checkpoint to restore the layer to.</param>
public sealed record TemporalRollbackPlanRequest(
    TemporalCheckpoint TargetCheckpoint);

/// <summary>
/// A normalized rollback execution request. Execution submits a NEW forward corrective operation through
/// the job runner; it never deletes history. The plan must report a runnable state and approval must be
/// supplied when the plan requires it.
/// </summary>
/// <param name="TargetCheckpoint">The checkpoint to restore the layer to.</param>
/// <param name="Approved">True when the operator has approved the rollback (required when the plan demands approval).</param>
/// <param name="Reason">Operator-supplied reason recorded in the corrective operation's attribution.</param>
public sealed record TemporalRollbackExecuteRequest(
    TemporalCheckpoint TargetCheckpoint,
    bool Approved,
    string? Reason);

/// <summary>
/// A handle to a submitted rollback corrective job (slice 5 of honua-server#1166). The rollback runs
/// asynchronously as a forward corrective operation; clients poll the job runner for completion. The
/// corrective operation creates a new checkpoint and preserves immutable history.
/// </summary>
/// <param name="JobId">Stable job identifier for polling the job runner.</param>
/// <param name="ServiceId">Owning service id.</param>
/// <param name="LayerId">Service-local layer index.</param>
/// <param name="TargetCheckpoint">The resolved checkpoint the rollback restores the layer to.</param>
/// <param name="Status">The job status at submission time.</param>
public sealed record TemporalRollbackJobHandle(
    string JobId,
    string ServiceId,
    int LayerId,
    ResolvedTemporalCheckpoint TargetCheckpoint,
    string Status);
