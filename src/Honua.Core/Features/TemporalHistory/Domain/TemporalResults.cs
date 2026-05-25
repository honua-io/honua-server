// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.TemporalHistory.Domain;

/// <summary>
/// Bounded paging request shared by history, diff, and timeline reads. Paging is cursor-based
/// (keyset) rather than offset-based so live writes cannot drift the page window.
/// </summary>
public sealed record TemporalPageRequest
{
    /// <summary>
    /// Default page size when a client does not request one.
    /// </summary>
    public const int DefaultLimit = 100;

    /// <summary>
    /// Maximum page size the server will honor.
    /// </summary>
    public const int MaxLimit = 1000;

    /// <summary>
    /// Requested page size, clamped to <see cref="MaxLimit"/>.
    /// </summary>
    public int Limit { get; init; } = DefaultLimit;

    /// <summary>
    /// Opaque continuation token returned by a previous page, or null for the first page.
    /// </summary>
    public string? Cursor { get; init; }

    /// <summary>
    /// Returns a normalized request with the limit clamped to the supported range.
    /// </summary>
    /// <returns>A normalized page request.</returns>
    public TemporalPageRequest Normalize()
        => this with { Limit = Limit <= 0 ? DefaultLimit : Math.Min(Limit, MaxLimit) };
}

/// <summary>
/// A single feature/row state at a temporal checkpoint. Attributes and geometry are returned in the
/// layer's source CRS; no reprojection is applied.
/// </summary>
public sealed record TemporalFeature
{
    /// <summary>
    /// Stable feature identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Feature geometry as GeoJSON in the source CRS, or null for non-spatial rows.
    /// </summary>
    public JsonElement? Geometry { get; init; }

    /// <summary>
    /// Non-system attributes for the feature at the requested checkpoint.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Attributes { get; init; }
        = new Dictionary<string, JsonElement>();
}

/// <summary>
/// Deterministic point-in-time snapshot of a layer as of a temporal cursor.
/// </summary>
public sealed record TemporalSnapshot
{
    /// <summary>
    /// Stable identifier of the layer.
    /// </summary>
    public required long LayerId { get; init; }

    /// <summary>
    /// The opaque cursor the snapshot was taken at.
    /// </summary>
    public required string At { get; init; }

    /// <summary>
    /// Resolved UTC instant the cursor maps to.
    /// </summary>
    public DateTimeOffset ResolvedAt { get; init; }

    /// <summary>
    /// Server time the snapshot was generated.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// SRID the geometries are expressed in.
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Feature states at the requested checkpoint, ordered deterministically by feature id.
    /// </summary>
    public IReadOnlyList<TemporalFeature> Items { get; init; } = [];

    /// <summary>
    /// Continuation token for the next page, or null when the snapshot is complete.
    /// </summary>
    public string? Next { get; init; }
}

/// <summary>
/// Actor/source attribution for a revision or change. Fields are null when not recorded or when
/// attribution is masked by policy.
/// </summary>
public sealed record TemporalAttribution
{
    /// <summary>
    /// Acting principal that produced the change.
    /// </summary>
    public string? Actor { get; init; }

    /// <summary>
    /// Source operation/release reference linked to the change.
    /// </summary>
    public string? SourceRef { get; init; }

    /// <summary>
    /// Change-set correlation identifier.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Instant the change was recorded.
    /// </summary>
    public DateTimeOffset? ChangedAt { get; init; }
}

/// <summary>
/// A field-level value change between two states.
/// </summary>
public sealed record TemporalFieldChange
{
    /// <summary>
    /// Field name that changed.
    /// </summary>
    public required string Field { get; init; }

    /// <summary>
    /// Value before the change, or null when absent.
    /// </summary>
    public JsonElement? Before { get; init; }

    /// <summary>
    /// Value after the change, or null when absent.
    /// </summary>
    public JsonElement? After { get; init; }
}

/// <summary>
/// Summary counts for a diff between two checkpoints.
/// </summary>
public sealed record TemporalDiffSummary
{
    /// <summary>
    /// Number of features present at the target but not the source checkpoint.
    /// </summary>
    public int Added { get; init; }

    /// <summary>
    /// Number of features present at the source but not the target checkpoint.
    /// </summary>
    public int Removed { get; init; }

    /// <summary>
    /// Number of features whose non-geometry attributes changed.
    /// </summary>
    public int AttributeChanged { get; init; }

    /// <summary>
    /// Number of features whose geometry changed.
    /// </summary>
    public int GeometryChanged { get; init; }
}

/// <summary>
/// A single feature's change between two checkpoints.
/// </summary>
public sealed record TemporalFeatureChange
{
    /// <summary>
    /// Stable feature identifier.
    /// </summary>
    public required string FeatureId { get; init; }

    /// <summary>
    /// Classification of the change.
    /// </summary>
    public required TemporalChangeKind ChangeKind { get; init; }

    /// <summary>
    /// Whether the geometry changed (may accompany an attribute change).
    /// </summary>
    public bool GeometryChanged { get; init; }

    /// <summary>
    /// Field-level attribute changes.
    /// </summary>
    public IReadOnlyList<TemporalFieldChange> FieldChanges { get; init; } = [];

    /// <summary>
    /// Attribution for the change, or null when unavailable or masked.
    /// </summary>
    public TemporalAttribution? Attribution { get; init; }

    /// <summary>
    /// Opaque operation/release reference linking the change to its source, when known.
    /// </summary>
    public string? OperationRef { get; init; }
}

/// <summary>
/// A diff between two temporal checkpoints with summary counts and a page of feature changes.
/// </summary>
public sealed record TemporalDiff
{
    /// <summary>
    /// Stable identifier of the layer.
    /// </summary>
    public required long LayerId { get; init; }

    /// <summary>
    /// The opaque source cursor.
    /// </summary>
    public required string From { get; init; }

    /// <summary>
    /// The opaque target cursor.
    /// </summary>
    public required string To { get; init; }

    /// <summary>
    /// Summary counts for the full diff.
    /// </summary>
    public TemporalDiffSummary Summary { get; init; } = new();

    /// <summary>
    /// Page of feature changes ordered deterministically by feature id.
    /// </summary>
    public IReadOnlyList<TemporalFeatureChange> Items { get; init; } = [];

    /// <summary>
    /// Continuation token for the next page, or null when complete.
    /// </summary>
    public string? Next { get; init; }
}

/// <summary>
/// A single revision of a feature in its timeline.
/// </summary>
public sealed record TemporalRevision
{
    /// <summary>
    /// Opaque cursor addressing this revision.
    /// </summary>
    public required string Cursor { get; init; }

    /// <summary>
    /// Revision operation (<c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c>).
    /// </summary>
    public required string Operation { get; init; }

    /// <summary>
    /// Attribution for the revision, or null when unavailable or masked.
    /// </summary>
    public TemporalAttribution? Attribution { get; init; }

    /// <summary>
    /// Field-level changes introduced by the revision relative to the prior revision.
    /// </summary>
    public IReadOnlyList<TemporalFieldChange> FieldChanges { get; init; } = [];

    /// <summary>
    /// Whether the revision changed the geometry.
    /// </summary>
    public bool GeometryChanged { get; init; }
}

/// <summary>
/// A per-feature timeline across revisions, newest first.
/// </summary>
public sealed record TemporalTimeline
{
    /// <summary>
    /// Stable identifier of the layer.
    /// </summary>
    public required long LayerId { get; init; }

    /// <summary>
    /// Stable feature identifier.
    /// </summary>
    public required string FeatureId { get; init; }

    /// <summary>
    /// Whether attribution was masked by policy for this read.
    /// </summary>
    public bool AttributionMasked { get; init; }

    /// <summary>
    /// Page of revisions ordered newest first.
    /// </summary>
    public IReadOnlyList<TemporalRevision> Revisions { get; init; } = [];

    /// <summary>
    /// Continuation token for the next page, or null when complete.
    /// </summary>
    public string? Next { get; init; }
}

/// <summary>
/// A named/derived checkpoint that can be used as a temporal cursor.
/// </summary>
public sealed record TemporalCheckpoint
{
    /// <summary>
    /// Opaque cursor token for the checkpoint.
    /// </summary>
    public required string Cursor { get; init; }

    /// <summary>
    /// Human-readable label.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Instant the checkpoint corresponds to.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Checkpoint kind token (for example <c>timestamp</c>, <c>release</c>, <c>job</c>, <c>edit-session</c>).
    /// </summary>
    public string? Kind { get; init; }
}

/// <summary>
/// A finding raised while validating a rollback plan.
/// </summary>
public sealed record TemporalFinding
{
    /// <summary>
    /// Stable machine-readable finding code.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Severity token (<c>info</c>, <c>warning</c>, <c>error</c>).
    /// </summary>
    public required string Severity { get; init; }

    /// <summary>
    /// Human-readable, client-safe message.
    /// </summary>
    public required string Message { get; init; }
}

/// <summary>
/// A plan describing whether and how a layer can be rolled back to a target checkpoint.
/// </summary>
public sealed record TemporalRollbackPlan
{
    /// <summary>
    /// Stable identifier of the layer.
    /// </summary>
    public required long LayerId { get; init; }

    /// <summary>
    /// The opaque target cursor to roll back to.
    /// </summary>
    public required string To { get; init; }

    /// <summary>
    /// Whether and how rollback can be applied.
    /// </summary>
    public required TemporalRollbackMode Mode { get; init; }

    /// <summary>
    /// Convenience flag: rollback is feasible when the mode is not <see cref="TemporalRollbackMode.Blocked"/>.
    /// </summary>
    public bool IsSupported => Mode != TemporalRollbackMode.Blocked;

    /// <summary>
    /// Number of features the corrective operation would affect.
    /// </summary>
    public int AffectedCount { get; init; }

    /// <summary>
    /// Whether explicit approval is required before execution.
    /// </summary>
    public bool RequiresApproval { get; init; }

    /// <summary>
    /// Whether the rollback must run through the job runner.
    /// </summary>
    public bool RequiresJob { get; init; }

    /// <summary>
    /// Whether the rollback requires an operator-supplied script.
    /// </summary>
    public bool RequiresScript { get; init; }

    /// <summary>
    /// Validation findings affecting feasibility.
    /// </summary>
    public IReadOnlyList<TemporalFinding> ValidationFindings { get; init; } = [];

    /// <summary>
    /// Schema/compatibility findings between the current and target states.
    /// </summary>
    public IReadOnlyList<TemporalFinding> CompatibilityFindings { get; init; } = [];
}

/// <summary>
/// Context passed to a rollback execution describing the governing job and actor.
/// </summary>
public sealed record TemporalRollbackContext
{
    /// <summary>
    /// Identifier of the job-run that governs the corrective operation.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Acting principal that approved the rollback.
    /// </summary>
    public string? Actor { get; init; }

    /// <summary>
    /// Operator-supplied reason for the rollback.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Correlation identifier stamped on the corrective change set.
    /// </summary>
    public string? CorrelationId { get; init; }
}

/// <summary>
/// Result of executing an approved rollback as a forward corrective operation.
/// </summary>
public sealed record TemporalRollbackResult
{
    /// <summary>
    /// Stable identifier of the layer.
    /// </summary>
    public required long LayerId { get; init; }

    /// <summary>
    /// Governing job-run identifier.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Number of corrective rows appended.
    /// </summary>
    public int AppliedCount { get; init; }

    /// <summary>
    /// Opaque cursor for the new checkpoint stamped by the corrective operation.
    /// </summary>
    public required string Checkpoint { get; init; }
}
