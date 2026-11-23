// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

// Reconcile's per-row overlap classification and the bounds its analysis reads.
internal sealed partial class PostgresVersionManager
{
    // The published prefix and the newest generation visible in this snapshot.
    private static async Task<(long Prefix, long VisibleHead)> ReadAnalysisBoundsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT honua.feature_change_read_horizon(),
                   GREATEST(honua.feature_change_read_horizon(), COALESCE(MAX(generation), 0))::BIGINT
            FROM honua.feature_changes
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static (OverlapConflict? Conflict, FieldMerge? Merge) ClassifyOverlap(
        OverlapRow row,
        VersionConflictDetection detection)
    {
        // A live DEFAULT row that still equals the base the overlay was built on (a version-wins rebase,
        // or a branch that captured it) has nothing to resolve, whatever either side did with it.
        var defaultChanged = ChangedFields(ParseAttributes(row.BaseAttrs), ParseAttributes(row.DefaultAttrs));
        if (row.DefaultOp != 3 && DefaultMatchesBase(defaultChanged, row.BaseGeom, row.DefaultGeom))
        {
            return (null, null);
        }

        // Delete-vs-update pairs are always genuine conflicts; both deleting the row converges.
        return (row.BranchOp, row.DefaultOp) switch
        {
            (2, 3) => (row.ToConflict(ReplicaConflictType.UpdateDelete, ImmutableArray<VersionConflictFieldDiff>.Empty), null),
            (3, 2) => (row.ToConflict(ReplicaConflictType.DeleteUpdate, ImmutableArray<VersionConflictFieldDiff>.Empty), null),
            (3, 3) => (null, null),
            _ => ClassifyUpdateOverlap(row, detection),
        };
    }

    private static (OverlapConflict? Conflict, FieldMerge? Merge) ClassifyUpdateOverlap(
        OverlapRow row,
        VersionConflictDetection detection)
    {
        // Update-vs-update: compute the field-level diffs and decide overlap vs auto-merge.
        var baseFields = ParseAttributes(row.BaseAttrs);
        var defaultFields = ParseAttributes(row.DefaultAttrs);
        var versionFields = ParseAttributes(row.VersionAttrs);

        var defaultChangedFields = ChangedFields(baseFields, defaultFields);
        var versionChangedFields = ChangedFields(baseFields, versionFields);
        var overlappingFields = defaultChangedFields.Intersect(versionChangedFields, StringComparer.Ordinal).ToArray();

        var geometryChangedOnBoth = !string.Equals(row.BaseGeom, row.DefaultGeom, StringComparison.Ordinal)
            && !string.Equals(row.BaseGeom, row.VersionGeom, StringComparison.Ordinal)
            && !string.Equals(row.DefaultGeom, row.VersionGeom, StringComparison.Ordinal);

        // By-attribute detection auto-merges disjoint edits and only flags genuinely-overlapping
        // edits (overlapping fields or geometry-vs-geometry). By-object detection flags ANY feature
        // edited on both sides since the merge base as a conflict, with no field-level auto-merge —
        // this is the documented Esri esriReconcileByObject behavior.
        var autoMergeable = detection == VersionConflictDetection.ByAttribute
            && overlappingFields.Length == 0
            && !geometryChangedOnBoth;

        if (autoMergeable)
        {
            // Disjoint edits: auto-merge DEFAULT's field changes (and a DEFAULT-only geometry change)
            // into the overlay so a post carries both sides without a conflict.
            var defaultOnlyGeometryChanged =
                !string.Equals(row.BaseGeom, row.DefaultGeom, StringComparison.Ordinal)
                && string.Equals(row.BaseGeom, row.VersionGeom, StringComparison.Ordinal);
            return (null, new FieldMerge(row.LayerId, row.ObjectId, defaultChangedFields, defaultFields, defaultOnlyGeometryChanged));
        }

        // Report the overlapping fields when present; under by-object detection a disjoint edit still
        // surfaces the union of both sides' changed fields so the conflict carries a useful three-way
        // diff for the manual-resolution UI.
        var reportedFields = overlappingFields.Length > 0
            ? overlappingFields
            : defaultChangedFields.Union(versionChangedFields, StringComparer.Ordinal).ToArray();
        var fieldDiffs = BuildFieldDiffs(reportedFields, baseFields, defaultFields, versionFields);
        var conflictType = reportedFields.Length > 0
            ? ReplicaConflictType.Attribute
            : ReplicaConflictType.Geometry;
        return (row.ToConflict(conflictType, fieldDiffs), null);
    }

    // DEFAULT still matches the base the overlay was built on (a rebase took it in): nothing to resolve.
    private static bool DefaultMatchesBase(HashSet<string> defaultChangedFields, string? baseGeom, string? defaultGeom)
        => defaultChangedFields.Count == 0 && string.Equals(baseGeom, defaultGeom, StringComparison.Ordinal);

    // One overlay row joined to DEFAULT's net change since the ancestor and its live row.
    private sealed class OverlapRow
    {
        public required int LayerId { get; init; }
        public required long ObjectId { get; init; }
        public required short BranchOp { get; init; }
        public required short DefaultOp { get; init; }
        public string? BaseAttrs { get; init; }
        public string? DefaultAttrs { get; init; }
        public string? VersionAttrs { get; init; }
        public string? BaseGeom { get; init; }
        public string? DefaultGeom { get; init; }
        public string? VersionGeom { get; init; }

        public static OverlapRow Read(NpgsqlDataReader reader) => new()
        {
            LayerId = reader.GetInt32(0),
            ObjectId = reader.GetInt64(1),
            BranchOp = reader.GetInt16(2),
            DefaultOp = reader.GetInt16(3),
            BaseAttrs = reader.IsDBNull(4) ? null : reader.GetString(4),
            DefaultAttrs = reader.IsDBNull(5) ? null : reader.GetString(5),
            VersionAttrs = reader.IsDBNull(6) ? null : reader.GetString(6),
            BaseGeom = reader.IsDBNull(7) ? null : reader.GetString(7),
            DefaultGeom = reader.IsDBNull(8) ? null : reader.GetString(8),
            VersionGeom = reader.IsDBNull(9) ? null : reader.GetString(9),
        };

        public OverlapConflict ToConflict(ReplicaConflictType type, ImmutableArray<VersionConflictFieldDiff> diffs)
            => new(LayerId, ObjectId, type, BaseAttrs, DefaultAttrs, VersionAttrs, BaseGeom, DefaultGeom, VersionGeom, diffs);
    }
}
