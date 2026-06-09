// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class PostgresVersionManager
{
    // Reconcile/post core (#1272 Track B Slice 2, ADR-0051; conflict-resolution layer #371). Reconcile
    // diffs DEFAULT feature_changes since the version's merge base against the version's tagged overlay
    // edits. For overlapping (layer_id, objectid) pairs it loads the three-way base/DEFAULT/version
    // images and:
    //   * auto-merges disjoint field-level edits (a feature edited in both the branch and DEFAULT but on
    //     DISJOINT attribute sets is merged into the overlay, not flagged);
    //   * classifies the rest as genuine conflicts (overlapping fields, geometry-vs-geometry,
    //     update-vs-delete) and, per #371, either auto-resolves them per the supplied policy
    //     (last-write-wins / version-wins / default-wins) or persists them as pending conflicts for
    //     manual review and blocks post.
    // A clean reconcile (no remaining conflicts) advances common_ancestor_gen. Post (only when
    // conflict-free) replays the version's net overlay rows onto the live DEFAULT features table in one
    // transaction and advances the generation through the existing change-tracking trigger.
    //
    // ADR-vs-reality note: ADR-0051 says post replays "via the shared IFeatureWriter". IFeatureWriter's
    // Feature model cannot preserve a branch-created row's explicit objectid (CreateAsync auto-assigns)
    // and round-trips attributes through a typed dictionary, so branch creates would lose their stable
    // OID and JSONB fidelity. Post therefore replays the net overlay rows with direct parameterized SQL
    // against the same base `features` table (and its change-tracking trigger), preserving objectid and
    // the raw JSONB image, inside a single transaction. Read/edit still flow through the shared pipeline.
    //
    // Deferred (out of scope for this slice, see PR): the Redis-backed version lock + Honua.Jobs async
    // execution wrapper (ADR-0051 / #1511). The reconcile/post engine here is synchronous; the job
    // wrapper will later call it under a lock.

    /// <inheritdoc />
    public async Task<VersionReconcileResult> ReconcileAsync(
        Guid versionId,
        VersionReconcilePolicy policy = VersionReconcilePolicy.None,
        CancellationToken cancellationToken = default)
    {
        var version = await LoadVersionAsync(versionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Version {versionId} does not exist.");

        await SetVersionStateAsync(versionId, VersionState.Reconciling, cancellationToken).ConfigureAwait(false);
        try
        {
            var currentGeneration = await GetCurrentGenerationAsync(cancellationToken).ConfigureAwait(false);
            var analysis = await AnalyzeOverlapsAsync(versionId, version.CommonAncestorGeneration, cancellationToken)
                .ConfigureAwait(false);

            // Disjoint field-level edits auto-merge into the overlay so post carries both sides.
            foreach (var merge in analysis.AutoMerges)
            {
                await ApplyFieldMergeAsync(versionId, merge, cancellationToken).ConfigureAwait(false);
            }

            var autoResolved = 0;
            var unresolved = ImmutableArray.CreateBuilder<VersionReconcileConflict>();
            foreach (var conflict in analysis.Conflicts)
            {
                if (policy != VersionReconcilePolicy.None &&
                    await TryAutoResolveAsync(versionId, conflict, policy, cancellationToken).ConfigureAwait(false))
                {
                    autoResolved++;
                    continue;
                }

                unresolved.Add(conflict.ToReport());
            }

            // Persist the unresolved set (manual review) and clear any stale pending rows that were
            // auto-merged or auto-resolved this run, so inspect/post see the current pending set.
            await PersistPendingConflictsAsync(versionId, unresolved, cancellationToken).ConfigureAwait(false);

            if (unresolved.Count == 0)
            {
                // Clean (or fully auto-resolved) reconcile: advance the merge base to current DEFAULT.
                await AdvanceCommonAncestorAsync(versionId, currentGeneration, cancellationToken).ConfigureAwait(false);
                return new VersionReconcileResult(
                    ImmutableArray<VersionReconcileConflict>.Empty,
                    CanPost: true,
                    NewCommonAncestorGeneration: currentGeneration,
                    AutoResolvedCount: autoResolved);
            }

            return new VersionReconcileResult(
                unresolved.ToImmutable(),
                CanPost: false,
                NewCommonAncestorGeneration: version.CommonAncestorGeneration,
                AutoResolvedCount: autoResolved);
        }
        finally
        {
            await SetVersionStateAsync(versionId, VersionState.Active, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VersionReconcileConflict>> GetPendingConflictsAsync(
        Guid versionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT layer_id, objectid, conflict_type,
                   base_attributes::text, default_attributes::text, version_attributes::text,
                   base_geometry_wkt, default_geometry_wkt, version_geometry_wkt, field_diffs::text
            FROM honua.version_conflicts
            WHERE version_id = @version AND status = 0
            ORDER BY layer_id, objectid
            """, connection);
        command.Parameters.AddWithValue("version", versionId);

        var results = new List<VersionReconcileConflict>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var fieldDiffsJson = reader.IsDBNull(9) ? null : reader.GetString(9);
            results.Add(new VersionReconcileConflict(
                reader.GetInt32(0),
                reader.GetInt64(1),
                (ReplicaConflictType)reader.GetInt16(2),
                BaseAttributesJson: reader.IsDBNull(3) ? null : reader.GetString(3),
                DefaultAttributesJson: reader.IsDBNull(4) ? null : reader.GetString(4),
                VersionAttributesJson: reader.IsDBNull(5) ? null : reader.GetString(5),
                BaseGeometryWkt: reader.IsDBNull(6) ? null : reader.GetString(6),
                DefaultGeometryWkt: reader.IsDBNull(7) ? null : reader.GetString(7),
                VersionGeometryWkt: reader.IsDBNull(8) ? null : reader.GetString(8),
                FieldDiffs: DeserializeFieldDiffs(fieldDiffsJson)));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<VersionConflictResolutionResult> ResolveConflictsAsync(
        Guid versionId,
        IReadOnlyList<VersionConflictResolution> resolutions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolutions);

        var resolved = 0;
        foreach (var resolution in resolutions)
        {
            if (await ApplyManualResolutionAsync(versionId, resolution, cancellationToken).ConfigureAwait(false))
            {
                resolved++;
            }
        }

        var remaining = await CountPendingConflictsAsync(versionId, cancellationToken).ConfigureAwait(false);
        return new VersionConflictResolutionResult(resolved, remaining, CanPost: remaining == 0);
    }

    /// <inheritdoc />
    public async Task<VersionPostResult> PostAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        var version = await LoadVersionAsync(versionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Version {versionId} does not exist.");

        // Post is refused while pending (unresolved) conflicts remain for the version; the caller must
        // reconcile clean, auto-resolve via policy, or manually resolve first.
        var pending = await CountPendingConflictsAsync(versionId, cancellationToken).ConfigureAwait(false);
        if (pending > 0)
        {
            return new VersionPostResult(Posted: false, AppliedChanges: 0, ServerGeneration: 0, BlockedByConflicts: true);
        }

        await SetVersionStateAsync(versionId, VersionState.Posting, cancellationToken).ConfigureAwait(false);
        try
        {
            var (applied, serverGeneration) = await ReplayOverlayOntoDefaultAsync(versionId, cancellationToken).ConfigureAwait(false);
            await AdvanceCommonAncestorAsync(versionId, serverGeneration, cancellationToken).ConfigureAwait(false);
            return new VersionPostResult(Posted: true, AppliedChanges: applied, ServerGeneration: serverGeneration, BlockedByConflicts: false);
        }
        finally
        {
            await SetVersionStateAsync(versionId, VersionState.Active, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Replays the version's net overlay rows onto the live DEFAULT features table inside a single
    /// transaction, preserving objectid and the raw JSONB/geometry image, then clears the overlay. The
    /// DEFAULT change-tracking trigger fires per row (no version GUC set), advancing the generation and
    /// recording NULL-version DEFAULT changes. Returns the applied row count and the post generation.
    /// </summary>
    private async Task<(int Applied, long ServerGeneration)> ReplayOverlayOntoDefaultAsync(
        Guid versionId,
        CancellationToken cancellationToken)
    {
        var featuresTable = DatabaseSchema.GetFeaturesTableName(_schemaName);

        var (txConnection, dbTransaction) = await _connectionProvider
            .OpenTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await using var _ = txConnection;
        var connection = txConnection.RequireNpgsqlConnection();
        var transaction = (NpgsqlTransaction)dbTransaction;
        await using var __ = transaction;

        var applied = 0;

        // Deletes: remove DEFAULT rows the branch deleted.
        await using (var deleteCommand = new NpgsqlCommand(
            $"DELETE FROM {featuresTable} f USING honua.version_edits ve " +
            "WHERE ve.version_id = @version AND ve.operation = 3 AND f.layer_id = ve.layer_id AND f.objectid = ve.objectid",
            connection)
        { Transaction = transaction })
        {
            deleteCommand.Parameters.AddWithValue("version", versionId);
            applied += await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Updates: overwrite DEFAULT geometry/attributes for branch-updated rows.
        await using (var updateCommand = new NpgsqlCommand(
            $"UPDATE {featuresTable} f SET geometry = ve.geometry, attributes = ve.attributes " +
            "FROM honua.version_edits ve " +
            "WHERE ve.version_id = @version AND ve.operation = 2 AND f.layer_id = ve.layer_id AND f.objectid = ve.objectid",
            connection)
        { Transaction = transaction })
        {
            updateCommand.Parameters.AddWithValue("version", versionId);
            applied += await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Inserts: add branch-created rows, preserving the branch-allocated objectid.
        await using (var insertCommand = new NpgsqlCommand(
            $"INSERT INTO {featuresTable} (objectid, layer_id, geometry, attributes) " +
            "SELECT ve.objectid, ve.layer_id, ve.geometry, ve.attributes FROM honua.version_edits ve " +
            "WHERE ve.version_id = @version AND ve.operation = 1",
            connection)
        { Transaction = transaction })
        {
            insertCommand.Parameters.AddWithValue("version", versionId);
            applied += await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Clear the overlay in the same transaction so a crash cannot leave a posted-but-not-cleared branch.
        await using (var clearCommand = new NpgsqlCommand(
            "DELETE FROM honua.version_edits WHERE version_id = @version", connection)
        { Transaction = transaction })
        {
            clearCommand.Parameters.AddWithValue("version", versionId);
            await clearCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long serverGeneration;
        await using (var genCommand = new NpgsqlCommand("SELECT last_value FROM honua.sync_generation", connection) { Transaction = transaction })
        {
            var result = await genCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            serverGeneration = result is long gen ? gen : 0;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (applied, serverGeneration);
    }

    private async Task<GdbVersion?> LoadVersionAsync(Guid versionId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT version_id, version_name, owner, parent_version, access, state, common_ancestor_gen, branch_gen, description, created_at, modified_at
            FROM honua.gdb_versions
            WHERE version_id = @id
            """, connection);
        command.Parameters.AddWithValue("id", versionId);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT last_value FROM honua.sync_generation", connection);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long gen ? gen : 0;
    }

    /// <summary>
    /// Loads the three-way (base/DEFAULT/version) images for every overlay row that shadows an
    /// (layer_id, objectid) DEFAULT also changed since the merge base, classifies each as a genuine
    /// conflict or an auto-mergeable disjoint-field edit, and returns both sets.
    /// </summary>
    private async Task<OverlapAnalysis> AnalyzeOverlapsAsync(
        Guid versionId,
        long commonAncestorGen,
        CancellationToken cancellationToken)
    {
        var featuresTable = DatabaseSchema.GetFeaturesTableName(_schemaName);
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);

        // default_changes: the net DEFAULT operation per (layer_id, objectid) since the merge base.
        // For each overlapping overlay row, join the live DEFAULT row (NULL when DEFAULT deleted it) and
        // the branch's captured base image. Geometry is rendered to WKT for display only.
        await using var command = new NpgsqlCommand($"""
            WITH default_changes AS (
                SELECT layer_id, objectid,
                       (array_agg(operation ORDER BY generation DESC))[1] AS last_op
                FROM honua.feature_changes
                WHERE version_id IS NULL AND generation > @ancestor
                GROUP BY layer_id, objectid
            )
            SELECT ve.layer_id, ve.objectid, ve.operation AS branch_op, dc.last_op AS default_op,
                   ve.base_attributes::text   AS base_attrs,
                   f.attributes::text         AS default_attrs,
                   ve.attributes::text        AS version_attrs,
                   ST_AsText(ve.base_geometry) AS base_geom,
                   ST_AsText(f.geometry)       AS default_geom,
                   ST_AsText(ve.geometry)      AS version_geom
            FROM honua.version_edits ve
            JOIN default_changes dc ON dc.layer_id = ve.layer_id AND dc.objectid = ve.objectid
            LEFT JOIN {featuresTable} f ON f.layer_id = ve.layer_id AND f.objectid = ve.objectid
            WHERE ve.version_id = @version
            """, connection);
        command.Parameters.AddWithValue("ancestor", commonAncestorGen);
        command.Parameters.AddWithValue("version", versionId);

        var conflicts = new List<OverlapConflict>();
        var merges = new List<FieldMerge>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var layerId = reader.GetInt32(0);
            var objectId = reader.GetInt64(1);
            var branchOp = reader.GetInt16(2);
            var defaultOp = reader.GetInt16(3);
            var baseAttrs = reader.IsDBNull(4) ? null : reader.GetString(4);
            var defaultAttrs = reader.IsDBNull(5) ? null : reader.GetString(5);
            var versionAttrs = reader.IsDBNull(6) ? null : reader.GetString(6);
            var baseGeom = reader.IsDBNull(7) ? null : reader.GetString(7);
            var defaultGeom = reader.IsDBNull(8) ? null : reader.GetString(8);
            var versionGeom = reader.IsDBNull(9) ? null : reader.GetString(9);

            // Delete-vs-update pairs are always genuine conflicts.
            if (branchOp == 2 && defaultOp == 3)
            {
                conflicts.Add(new OverlapConflict(layerId, objectId, ReplicaConflictType.UpdateDelete,
                    baseAttrs, defaultAttrs, versionAttrs, baseGeom, defaultGeom, versionGeom, ImmutableArray<VersionConflictFieldDiff>.Empty));
                continue;
            }

            if (branchOp == 3 && defaultOp == 2)
            {
                conflicts.Add(new OverlapConflict(layerId, objectId, ReplicaConflictType.DeleteUpdate,
                    baseAttrs, defaultAttrs, versionAttrs, baseGeom, defaultGeom, versionGeom, ImmutableArray<VersionConflictFieldDiff>.Empty));
                continue;
            }

            if (branchOp == 3 && defaultOp == 3)
            {
                // Both deleted the same row: a no-op convergence, not a conflict.
                continue;
            }

            // Update-vs-update: compute the field-level diffs and decide overlap vs auto-merge.
            var baseFields = ParseAttributes(baseAttrs);
            var defaultFields = ParseAttributes(defaultAttrs);
            var versionFields = ParseAttributes(versionAttrs);

            var defaultChangedFields = ChangedFields(baseFields, defaultFields);
            var versionChangedFields = ChangedFields(baseFields, versionFields);
            var overlappingFields = defaultChangedFields.Intersect(versionChangedFields, StringComparer.Ordinal).ToArray();

            var geometryChangedOnBoth = !string.Equals(baseGeom, defaultGeom, StringComparison.Ordinal)
                && !string.Equals(baseGeom, versionGeom, StringComparison.Ordinal)
                && !string.Equals(defaultGeom, versionGeom, StringComparison.Ordinal);

            if (overlappingFields.Length == 0 && !geometryChangedOnBoth)
            {
                // Disjoint edits: auto-merge DEFAULT's field changes (and a DEFAULT-only geometry change)
                // into the overlay so a post carries both sides without a conflict.
                var defaultOnlyGeometryChanged =
                    !string.Equals(baseGeom, defaultGeom, StringComparison.Ordinal)
                    && string.Equals(baseGeom, versionGeom, StringComparison.Ordinal);
                merges.Add(new FieldMerge(layerId, objectId, defaultChangedFields, defaultFields, defaultOnlyGeometryChanged));
                continue;
            }

            var fieldDiffs = BuildFieldDiffs(overlappingFields, baseFields, defaultFields, versionFields);
            var conflictType = overlappingFields.Length > 0
                ? ReplicaConflictType.Attribute
                : ReplicaConflictType.Geometry;
            conflicts.Add(new OverlapConflict(layerId, objectId, conflictType,
                baseAttrs, defaultAttrs, versionAttrs, baseGeom, defaultGeom, versionGeom, fieldDiffs));
        }

        return new OverlapAnalysis(conflicts, merges);
    }

    /// <summary>
    /// Auto-merges DEFAULT's disjoint field changes (and an optional DEFAULT-only geometry change) into
    /// the branch overlay row so the subsequent post carries both sides of a non-conflicting edit.
    /// </summary>
    private async Task ApplyFieldMergeAsync(Guid versionId, FieldMerge merge, CancellationToken cancellationToken)
    {
        if (merge.DefaultChangedFields.Count == 0 && !merge.MergeDefaultGeometry)
        {
            return;
        }

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Build the JSON patch of DEFAULT's changed fields and merge it onto the overlay attributes with
        // the JSONB concat operator (||) — overlay keys win for any non-changed field, DEFAULT's changed
        // fields are layered in. A field DEFAULT removed is rendered as a JSON null. A DEFAULT-only
        // geometry change is copied from the live row.
        var patchJson = BuildMergePatchJson(merge.DefaultChangedFields, merge.DefaultFields);
        var geometryClause = merge.MergeDefaultGeometry
            ? ", geometry = (SELECT f.geometry FROM " + DatabaseSchema.GetFeaturesTableName(_schemaName) +
              " f WHERE f.layer_id = @layer AND f.objectid = @objectid)"
            : string.Empty;

        await using var command = new NpgsqlCommand(
            "UPDATE honua.version_edits " +
            "SET attributes = COALESCE(attributes, '{}'::jsonb) || @patch::jsonb, modified_at = now()" +
            geometryClause + " " +
            "WHERE version_id = @version AND layer_id = @layer AND objectid = @objectid",
            connection);
        command.Parameters.AddWithValue("patch", patchJson);
        command.Parameters.AddWithValue("version", versionId);
        command.Parameters.AddWithValue("layer", merge.LayerId);
        command.Parameters.AddWithValue("objectid", merge.ObjectId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deterministically resolves a single conflict per the supplied policy by rewriting the overlay row
    /// to the winning side, so a subsequent post carries the resolved image. Returns false (leaving the
    /// conflict for manual review) when a policy cannot be applied to the pairing.
    /// </summary>
    private async Task<bool> TryAutoResolveAsync(
        Guid versionId,
        OverlapConflict conflict,
        VersionReconcilePolicy policy,
        CancellationToken cancellationToken)
    {
        var side = policy switch
        {
            VersionReconcilePolicy.VersionWins => VersionConflictResolutionChoice.TakeVersion,
            VersionReconcilePolicy.DefaultWins => VersionConflictResolutionChoice.TakeDefault,
            VersionReconcilePolicy.LastWriteWins => await ResolveLastWriteWinsSideAsync(versionId, conflict, cancellationToken).ConfigureAwait(false),
            _ => (VersionConflictResolutionChoice?)null,
        };

        if (side is null)
        {
            return false;
        }

        await ApplyResolutionToOverlayAsync(versionId, conflict.LayerId, conflict.ObjectId, side.Value, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Resolves the last-write-wins side by comparing the latest change generation produced by the
    /// branch overlay against the latest DEFAULT change generation for the feature since the merge base.
    /// </summary>
    private async Task<VersionConflictResolutionChoice> ResolveLastWriteWinsSideAsync(
        Guid versionId,
        OverlapConflict conflict,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT
                COALESCE((SELECT MAX(generation) FROM honua.feature_changes
                          WHERE version_id = @version AND layer_id = @layer AND objectid = @objectid), 0) AS branch_gen,
                COALESCE((SELECT MAX(generation) FROM honua.feature_changes
                          WHERE version_id IS NULL AND layer_id = @layer AND objectid = @objectid), 0) AS default_gen
            """, connection);
        command.Parameters.AddWithValue("version", versionId);
        command.Parameters.AddWithValue("layer", conflict.LayerId);
        command.Parameters.AddWithValue("objectid", conflict.ObjectId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var branchGen = reader.GetInt64(0);
        var defaultGen = reader.GetInt64(1);

        // The branch wins ties: an explicit branch edit is the more recent intentional change.
        return branchGen >= defaultGen
            ? VersionConflictResolutionChoice.TakeVersion
            : VersionConflictResolutionChoice.TakeDefault;
    }

    /// <summary>
    /// Applies one operator-submitted manual resolution: rewrites the overlay row to the chosen side and
    /// marks the persisted pending conflict resolved. Returns false when there was no pending conflict.
    /// </summary>
    private async Task<bool> ApplyManualResolutionAsync(
        Guid versionId,
        VersionConflictResolution resolution,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var markCommand = new NpgsqlCommand("""
            UPDATE honua.version_conflicts
            SET status = 1, resolution_choice = @choice, resolved_at = now()
            WHERE version_id = @version AND layer_id = @layer AND objectid = @objectid AND status = 0
            """, connection);
        markCommand.Parameters.AddWithValue("choice", (short)resolution.Choice);
        markCommand.Parameters.AddWithValue("version", versionId);
        markCommand.Parameters.AddWithValue("layer", resolution.LayerId);
        markCommand.Parameters.AddWithValue("objectid", resolution.ObjectId);
        var affected = await markCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            return false;
        }

        await ApplyResolutionToOverlayAsync(versionId, resolution.LayerId, resolution.ObjectId, resolution.Choice, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Rewrites the branch overlay row for the resolved feature so the post replays the chosen side:
    /// take-version keeps the overlay as-is; take-default rewrites the overlay to the live DEFAULT image
    /// (or removes the overlay row when DEFAULT deleted it); take-base rewrites to the captured base
    /// image. All keep the overlay-mediated post path so DEFAULT data is untouched until post.
    /// </summary>
    private async Task ApplyResolutionToOverlayAsync(
        Guid versionId,
        int layerId,
        long objectId,
        VersionConflictResolutionChoice choice,
        CancellationToken cancellationToken)
    {
        if (choice == VersionConflictResolutionChoice.TakeVersion)
        {
            // The branch overlay already carries the version image; nothing to rewrite.
            return;
        }

        var featuresTable = DatabaseSchema.GetFeaturesTableName(_schemaName);
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (choice == VersionConflictResolutionChoice.TakeDefault)
        {
            // Take DEFAULT: if DEFAULT still has the row, set the overlay to an UPDATE carrying DEFAULT's
            // image (a no-op post against DEFAULT); if DEFAULT deleted it, drop the overlay row so the
            // branch no longer shadows/posts the feature.
            await using var command = new NpgsqlCommand($"""
                WITH d AS (
                    SELECT geometry, attributes FROM {featuresTable}
                    WHERE layer_id = @layer AND objectid = @objectid
                ),
                upd AS (
                    UPDATE honua.version_edits ve
                    SET operation = 2, geometry = d.geometry, attributes = d.attributes, modified_at = now()
                    FROM d
                    WHERE ve.version_id = @version AND ve.layer_id = @layer AND ve.objectid = @objectid
                    RETURNING 1
                )
                DELETE FROM honua.version_edits ve
                WHERE ve.version_id = @version AND ve.layer_id = @layer AND ve.objectid = @objectid
                  AND NOT EXISTS (SELECT 1 FROM d)
                """, connection);
            command.Parameters.AddWithValue("version", versionId);
            command.Parameters.AddWithValue("layer", layerId);
            command.Parameters.AddWithValue("objectid", objectId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // TakeBase: rewrite the overlay to the captured base image (an UPDATE that restores the
        // common-ancestor state on post).
        await using var baseCommand = new NpgsqlCommand("""
            UPDATE honua.version_edits
            SET operation = 2, geometry = base_geometry, attributes = base_attributes, modified_at = now()
            WHERE version_id = @version AND layer_id = @layer AND objectid = @objectid
            """, connection);
        baseCommand.Parameters.AddWithValue("version", versionId);
        baseCommand.Parameters.AddWithValue("layer", layerId);
        baseCommand.Parameters.AddWithValue("objectid", objectId);
        await baseCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the version's persisted pending conflict set with the supplied unresolved conflicts:
    /// clears stale pending rows (auto-merged / auto-resolved this run) and inserts the current set.
    /// </summary>
    private async Task PersistPendingConflictsAsync(
        Guid versionId,
        IReadOnlyCollection<VersionReconcileConflict> conflicts,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (var clear = new NpgsqlCommand(
            "DELETE FROM honua.version_conflicts WHERE version_id = @version AND status = 0", connection))
        {
            clear.Parameters.AddWithValue("version", versionId);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var conflict in conflicts)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO honua.version_conflicts
                    (version_id, layer_id, objectid, conflict_type, status,
                     base_attributes, default_attributes, version_attributes,
                     base_geometry_wkt, default_geometry_wkt, version_geometry_wkt, field_diffs)
                VALUES (@version, @layer, @objectid, @type, 0,
                        @base_attrs::jsonb, @default_attrs::jsonb, @version_attrs::jsonb,
                        @base_geom, @default_geom, @version_geom, @field_diffs::jsonb)
                """, connection);
            insert.Parameters.AddWithValue("version", versionId);
            insert.Parameters.AddWithValue("layer", conflict.LayerId);
            insert.Parameters.AddWithValue("objectid", conflict.ObjectId);
            insert.Parameters.AddWithValue("type", (short)conflict.ConflictType);
            insert.Parameters.AddWithValue("base_attrs", (object?)conflict.BaseAttributesJson ?? DBNull.Value);
            insert.Parameters.AddWithValue("default_attrs", (object?)conflict.DefaultAttributesJson ?? DBNull.Value);
            insert.Parameters.AddWithValue("version_attrs", (object?)conflict.VersionAttributesJson ?? DBNull.Value);
            insert.Parameters.AddWithValue("base_geom", (object?)conflict.BaseGeometryWkt ?? DBNull.Value);
            insert.Parameters.AddWithValue("default_geom", (object?)conflict.DefaultGeometryWkt ?? DBNull.Value);
            insert.Parameters.AddWithValue("version_geom", (object?)conflict.VersionGeometryWkt ?? DBNull.Value);
            var fieldDiffsJson = conflict.FieldDiffs.IsDefaultOrEmpty
                ? null
                : JsonSerializer.Serialize(conflict.FieldDiffs.ToArray(), VersionConflictJson.FieldDiffArray);
            insert.Parameters.AddWithValue("field_diffs", (object?)fieldDiffsJson ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<int> CountPendingConflictsAsync(Guid versionId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM honua.version_conflicts WHERE version_id = @version AND status = 0", connection);
        command.Parameters.AddWithValue("version", versionId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long count ? (int)count : 0;
    }

    private async Task AdvanceCommonAncestorAsync(Guid versionId, long generation, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "UPDATE honua.gdb_versions SET common_ancestor_gen = @gen, modified_at = now() WHERE version_id = @id",
            connection);
        command.Parameters.AddWithValue("gen", generation);
        command.Parameters.AddWithValue("id", versionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SetVersionStateAsync(Guid versionId, VersionState state, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "UPDATE honua.gdb_versions SET state = @state, modified_at = now() WHERE version_id = @id", connection);
        command.Parameters.AddWithValue("state", (short)state);
        command.Parameters.AddWithValue("id", versionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- Field-level diff helpers --------------------------------------------------------------

    private static ImmutableDictionary<string, JsonElement> ParseAttributes(string? attributesJson)
    {
        if (string.IsNullOrWhiteSpace(attributesJson))
        {
            return ImmutableDictionary<string, JsonElement>.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(attributesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ImmutableDictionary<string, JsonElement>.Empty;
            }

            var builder = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                builder[property.Name] = property.Value.Clone();
            }

            return builder.ToImmutable();
        }
        catch (JsonException)
        {
            return ImmutableDictionary<string, JsonElement>.Empty;
        }
    }

    /// <summary>
    /// Returns the set of attribute names whose value differs between the base image and a target image
    /// (added, removed, or value-changed keys), compared by canonical JSON text.
    /// </summary>
    private static HashSet<string> ChangedFields(
        ImmutableDictionary<string, JsonElement> baseFields,
        ImmutableDictionary<string, JsonElement> targetFields)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in baseFields.Keys.Union(targetFields.Keys, StringComparer.Ordinal))
        {
            var hasBase = baseFields.TryGetValue(key, out var baseValue);
            var hasTarget = targetFields.TryGetValue(key, out var targetValue);
            if (hasBase != hasTarget || (hasBase && hasTarget && !ElementsEqual(baseValue, targetValue)))
            {
                changed.Add(key);
            }
        }

        return changed;
    }

    private static bool ElementsEqual(JsonElement left, JsonElement right)
        => string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);

    private static ImmutableArray<VersionConflictFieldDiff> BuildFieldDiffs(
        string[] fields,
        ImmutableDictionary<string, JsonElement> baseFields,
        ImmutableDictionary<string, JsonElement> defaultFields,
        ImmutableDictionary<string, JsonElement> versionFields)
    {
        if (fields.Length == 0)
        {
            return ImmutableArray<VersionConflictFieldDiff>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<VersionConflictFieldDiff>(fields.Length);
        foreach (var name in fields.OrderBy(n => n, StringComparer.Ordinal))
        {
            builder.Add(new VersionConflictFieldDiff(
                name,
                Render(baseFields, name),
                Render(defaultFields, name),
                Render(versionFields, name)));
        }

        return builder.ToImmutable();
    }

    private static string? Render(ImmutableDictionary<string, JsonElement> fields, string name)
        => fields.TryGetValue(name, out var value) ? value.GetRawText() : null;

    /// <summary>
    /// Builds a JSON object literal of DEFAULT's changed fields for the JSONB merge patch, rendering a
    /// field DEFAULT removed as a JSON null. Values are emitted from their raw JSON text so the merged
    /// value preserves DEFAULT's original JSON shape (numbers, strings, nested objects).
    /// </summary>
    private static string BuildMergePatchJson(
        IReadOnlyCollection<string> changedFields,
        ImmutableDictionary<string, JsonElement> defaultFields)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var field in changedFields)
            {
                writer.WritePropertyName(field);
                if (defaultFields.TryGetValue(field, out var value))
                {
                    value.WriteTo(writer);
                }
                else
                {
                    writer.WriteNullValue();
                }
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static ImmutableArray<VersionConflictFieldDiff> DeserializeFieldDiffs(string? fieldDiffsJson)
    {
        if (string.IsNullOrWhiteSpace(fieldDiffsJson))
        {
            return ImmutableArray<VersionConflictFieldDiff>.Empty;
        }

        var array = JsonSerializer.Deserialize(fieldDiffsJson, VersionConflictJson.FieldDiffArray);
        return array is null or { Length: 0 }
            ? ImmutableArray<VersionConflictFieldDiff>.Empty
            : ImmutableArray.Create(array);
    }

    // ---- Internal analysis types ---------------------------------------------------------------

    private sealed record OverlapAnalysis(
        IReadOnlyList<OverlapConflict> Conflicts,
        IReadOnlyList<FieldMerge> AutoMerges);

    private sealed record OverlapConflict(
        int LayerId,
        long ObjectId,
        ReplicaConflictType ConflictType,
        string? BaseAttributesJson,
        string? DefaultAttributesJson,
        string? VersionAttributesJson,
        string? BaseGeometryWkt,
        string? DefaultGeometryWkt,
        string? VersionGeometryWkt,
        ImmutableArray<VersionConflictFieldDiff> FieldDiffs)
    {
        public VersionReconcileConflict ToReport() => new(
            LayerId,
            ObjectId,
            ConflictType,
            BaseAttributesJson,
            DefaultAttributesJson,
            VersionAttributesJson,
            BaseGeometryWkt,
            DefaultGeometryWkt,
            VersionGeometryWkt,
            FieldDiffs);
    }

    private sealed record FieldMerge(
        int LayerId,
        long ObjectId,
        IReadOnlyCollection<string> DefaultChangedFields,
        ImmutableDictionary<string, JsonElement> DefaultFields,
        bool MergeDefaultGeometry);
}
