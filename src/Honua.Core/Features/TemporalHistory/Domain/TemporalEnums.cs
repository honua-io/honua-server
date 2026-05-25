// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.TemporalHistory.Domain;

/// <summary>
/// Backend strategy a temporal source uses to reconstruct history for a layer.
/// </summary>
public enum TemporalSourceKind
{
    /// <summary>
    /// A parallel append-only audit-log table records every revision with before/after attributes.
    /// </summary>
    AuditLog,

    /// <summary>
    /// A system-versioned table carries a <c>tstzrange</c> system-period column per row.
    /// </summary>
    TemporalTable,

    /// <summary>
    /// A delta/change-log file or table records committed change sets.
    /// </summary>
    DeltaLog
}

/// <summary>
/// Declares how a temporal source tolerates schema change across history.
/// </summary>
public enum SchemaEvolutionPolicy
{
    /// <summary>
    /// The schema is fixed; history reads assume identical columns across all revisions.
    /// </summary>
    Fixed,

    /// <summary>
    /// Only additive schema changes are expected; older revisions omit newer fields.
    /// </summary>
    Additive,

    /// <summary>
    /// Compatible schema changes are tolerated through the configured attribute mapping.
    /// </summary>
    Compatible
}

/// <summary>
/// Classifies how a single feature changed between two temporal checkpoints.
/// </summary>
public enum TemporalChangeKind
{
    /// <summary>
    /// The feature exists at the target checkpoint but not at the source checkpoint.
    /// </summary>
    Added,

    /// <summary>
    /// The feature exists at the source checkpoint but not at the target checkpoint.
    /// </summary>
    Removed,

    /// <summary>
    /// The feature exists at both checkpoints and at least one non-geometry attribute changed.
    /// </summary>
    AttributeChanged,

    /// <summary>
    /// The feature exists at both checkpoints and only its geometry changed.
    /// </summary>
    GeometryChanged
}

/// <summary>
/// Reports whether and how a rollback to a target checkpoint can be applied.
/// </summary>
public enum TemporalRollbackMode
{
    /// <summary>
    /// Rollback can be applied directly as a corrective forward operation.
    /// </summary>
    Supported,

    /// <summary>
    /// Rollback cannot be applied; blocking findings are present.
    /// </summary>
    Blocked,

    /// <summary>
    /// Rollback requires an operator-supplied migration script.
    /// </summary>
    ScriptRequired,

    /// <summary>
    /// Rollback must run through the Honua job runner because of its size or scope.
    /// </summary>
    JobRequired,

    /// <summary>
    /// Rollback requires manual operator intervention outside the temporal contract.
    /// </summary>
    Manual
}

/// <summary>
/// Distinct temporal-history operations that carry independent authorization.
/// </summary>
public enum TemporalOperation
{
    /// <summary>
    /// Reads the current/latest state and capability discovery.
    /// </summary>
    CurrentRead,

    /// <summary>
    /// Reads historical (as-of and checkpoint) state.
    /// </summary>
    HistoryRead,

    /// <summary>
    /// Reads diffs between two checkpoints.
    /// </summary>
    DiffRead,

    /// <summary>
    /// Reads per-feature timelines with attribution.
    /// </summary>
    TimelineRead,

    /// <summary>
    /// Generates a rollback plan.
    /// </summary>
    RollbackPlan,

    /// <summary>
    /// Executes an approved rollback.
    /// </summary>
    RollbackExecute
}
