// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// Operations a metadata-release schema script can declare for its additive forward change and
/// its reversible inverse. Kept intentionally small for the additive layer-evolution path; the
/// SnapshotRestore class is handled out-of-band and is not represented here.
/// </summary>
public enum MetadataReleaseScriptOperationKind
{
    /// <summary>
    /// Add a nullable column/field to an existing resource. Additive and reversible.
    /// </summary>
    AddColumn,

    /// <summary>
    /// Drop a previously added column/field. Used as the inverse of <see cref="AddColumn"/>.
    /// </summary>
    DropColumn
}

/// <summary>
/// One executable schema operation in a metadata-release script. Unlike the analyzer-only
/// <c>MetadataDataScriptEntry.Reversible</c> flag, this carries an actually executable
/// representation (operation kind + target resource/field) so the reconciler can both apply the
/// additive forward change and execute the reversible inverse on rollback.
/// </summary>
public sealed record MetadataReleaseScriptOperation
{
    /// <summary>
    /// Operation kind.
    /// </summary>
    public required MetadataReleaseScriptOperationKind Kind { get; init; }

    /// <summary>
    /// Semantic identifier of the resource whose schema is changed.
    /// </summary>
    public required string ResourceSemanticId { get; init; }

    /// <summary>
    /// Name of the field/column added or dropped.
    /// </summary>
    public required string FieldName { get; init; }

    /// <summary>
    /// Canonical field type for an add operation (for example <c>String</c> or <c>Integer</c>).
    /// Ignored for drop operations.
    /// </summary>
    public string? FieldType { get; init; }

    /// <summary>
    /// Whether the added field is nullable. Additive evolution requires nullable adds so existing
    /// rows remain valid without a backfill.
    /// </summary>
    public bool Nullable { get; init; } = true;
}

/// <summary>
/// Executable schema script for a metadata release. Pairs the additive forward operations with
/// their reversible inverse so the lifecycle can advance forward and roll back without a snapshot.
/// </summary>
public sealed record MetadataReleaseScript
{
    /// <summary>
    /// Stable script identifier, aligned with the analyzer's <c>MetadataDataScriptEntry.ScriptId</c>.
    /// </summary>
    public required string ScriptId { get; init; }

    /// <summary>
    /// Whether the script declares a fully reversible inverse. Forward-only scripts are not eligible
    /// for the additive reversible-rollback path.
    /// </summary>
    public bool Reversible { get; init; } = true;

    /// <summary>
    /// Ordered forward operations applied during the ScriptMigration stage.
    /// </summary>
    public required IReadOnlyList<MetadataReleaseScriptOperation> ForwardOperations { get; init; }

    /// <summary>
    /// Ordered inverse operations executed on reversible rollback. When empty the inverse is derived
    /// from the forward operations (an add becomes a drop of the same field).
    /// </summary>
    public IReadOnlyList<MetadataReleaseScriptOperation> InverseOperations { get; init; }
        = Array.Empty<MetadataReleaseScriptOperation>();

    /// <summary>
    /// Computes the inverse operations: the declared inverse when present, otherwise the derived
    /// inverse of each forward operation in reverse order (add → drop).
    /// </summary>
    public IReadOnlyList<MetadataReleaseScriptOperation> ResolveInverseOperations()
    {
        if (InverseOperations.Count > 0)
        {
            return InverseOperations;
        }

        var derived = new List<MetadataReleaseScriptOperation>(ForwardOperations.Count);
        for (var index = ForwardOperations.Count - 1; index >= 0; index--)
        {
            var forward = ForwardOperations[index];
            if (forward.Kind == MetadataReleaseScriptOperationKind.AddColumn)
            {
                derived.Add(new MetadataReleaseScriptOperation
                {
                    Kind = MetadataReleaseScriptOperationKind.DropColumn,
                    ResourceSemanticId = forward.ResourceSemanticId,
                    FieldName = forward.FieldName
                });
            }
        }

        return derived;
    }
}

/// <summary>
/// Desired state for an additive metadata-release lifecycle, carried as parameters on the workflow
/// operation so the reconciler can resolve the executable script, target environment, and the new
/// field the Smoke stage must observe. This is the additive-path companion to
/// <see cref="MetadataReleaseContext"/>.
/// </summary>
public sealed record MetadataReleaseExecutionPlan
{
    /// <summary>
    /// Release package identifier this lifecycle advances.
    /// </summary>
    public required string PackageId { get; init; }

    /// <summary>
    /// Target environment whose current graph snapshot is the rollback target.
    /// </summary>
    public required string TargetEnvironment { get; init; }

    /// <summary>
    /// Semantic identifier of the resource/layer being evolved and smoke-checked.
    /// </summary>
    public required string ResourceSemanticId { get; init; }

    /// <summary>
    /// Name of the newly added field the Smoke stage asserts is present in the activated schema.
    /// </summary>
    public required string NewFieldName { get; init; }

    /// <summary>
    /// Executable additive schema script with its reversible inverse.
    /// </summary>
    public required MetadataReleaseScript Script { get; init; }

    /// <summary>
    /// Optional ETL/data-populate workload identifier dispatched as a job after ScriptMigration.
    /// Null when the additive change needs no data populate.
    /// </summary>
    public string? DataPopulateWorkloadId { get; init; }
}
