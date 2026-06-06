// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class PostgresVersionManager
{
    // Reconcile/post core (#1272 Track B Slice 2, ADR-0051). Reconcile diffs DEFAULT feature_changes since
    // the version's merge base against the version's tagged overlay edits and classifies overlapping-OID
    // conflicts using the #1287 conflict taxonomy. A clean reconcile advances common_ancestor_gen. Post
    // (only when conflict-free) replays the version's net overlay rows onto the live DEFAULT features table
    // in one transaction and advances the generation through the existing change-tracking trigger.
    //
    // ADR-vs-reality note: ADR-0051 says post replays "via the shared IFeatureWriter". IFeatureWriter's
    // Feature model cannot preserve a branch-created row's explicit objectid (CreateAsync auto-assigns) and
    // round-trips attributes through a typed dictionary, so branch creates would lose their stable OID and
    // JSONB fidelity. Post therefore replays the net overlay rows with direct parameterized SQL against the
    // same base `features` table (and its change-tracking trigger), preserving objectid and the raw JSONB
    // image, inside a single transaction. Read/edit still flow through the shared pipeline.
    //
    // Deferred (out of scope for this slice, see PR): the Redis-backed version lock + Honua.Jobs async
    // execution wrapper, #371 auto-resolution policies, and durable #1287 ReplicaConflictRecord persistence.
    // The reconcile/post engine here is synchronous; the job wrapper will later call it under a lock.

    /// <inheritdoc />
    public async Task<VersionReconcileResult> ReconcileAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        var version = await LoadVersionAsync(versionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Version {versionId} does not exist.");

        await SetVersionStateAsync(versionId, VersionState.Reconciling, cancellationToken).ConfigureAwait(false);
        try
        {
            var currentGeneration = await GetCurrentGenerationAsync(cancellationToken).ConfigureAwait(false);
            var conflicts = await DetectConflictsAsync(versionId, version.CommonAncestorGeneration, cancellationToken).ConfigureAwait(false);

            if (conflicts.IsEmpty)
            {
                // Clean reconcile: advance the merge base to the current DEFAULT generation.
                await AdvanceCommonAncestorAsync(versionId, currentGeneration, cancellationToken).ConfigureAwait(false);
                return new VersionReconcileResult(conflicts, CanPost: true, NewCommonAncestorGeneration: currentGeneration);
            }

            return new VersionReconcileResult(conflicts, CanPost: false, NewCommonAncestorGeneration: version.CommonAncestorGeneration);
        }
        finally
        {
            await SetVersionStateAsync(versionId, VersionState.Active, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<VersionPostResult> PostAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        var version = await LoadVersionAsync(versionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Version {versionId} does not exist.");

        // Post is refused while unresolved conflicts remain since the merge base; the caller must
        // reconcile clean first.
        var conflicts = await DetectConflictsAsync(versionId, version.CommonAncestorGeneration, cancellationToken).ConfigureAwait(false);
        if (!conflicts.IsEmpty)
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

    private async Task<ImmutableArray<VersionReconcileConflict>> DetectConflictsAsync(
        Guid versionId,
        long commonAncestorGen,
        CancellationToken cancellationToken)
    {
        // Conflicts are (layer_id, objectid) pairs the branch shadows that DEFAULT also changed since the
        // merge base. The classification pairs the branch overlay operation with the net DEFAULT operation:
        //   DEFAULT delete + branch update  -> DeleteUpdate
        //   branch delete  + DEFAULT update -> UpdateDelete
        //   otherwise overlapping edit      -> Attribute (generic overlap; finer geometry/attribute
        //                                      classification is owned by #371's policy layer).
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            WITH default_changes AS (
                SELECT layer_id, objectid,
                       (array_agg(operation ORDER BY generation DESC))[1] AS last_op
                FROM honua.feature_changes
                WHERE version_id IS NULL AND generation > @ancestor
                GROUP BY layer_id, objectid
            )
            SELECT ve.layer_id, ve.objectid, ve.operation AS branch_op, dc.last_op AS default_op
            FROM honua.version_edits ve
            JOIN default_changes dc ON dc.layer_id = ve.layer_id AND dc.objectid = ve.objectid
            WHERE ve.version_id = @version
            """, connection);
        command.Parameters.AddWithValue("ancestor", commonAncestorGen);
        command.Parameters.AddWithValue("version", versionId);

        var conflicts = ImmutableArray.CreateBuilder<VersionReconcileConflict>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var layerId = reader.GetInt32(0);
            var objectId = reader.GetInt64(1);
            var branchOp = reader.GetInt16(2);
            var defaultOp = reader.GetInt16(3);

            var type = (branchOp, defaultOp) switch
            {
                (2, 3) => ReplicaConflictType.UpdateDelete,  // branch updated, DEFAULT deleted
                (3, 2) => ReplicaConflictType.DeleteUpdate,  // branch deleted, DEFAULT updated
                _ => ReplicaConflictType.Attribute,
            };

            conflicts.Add(new VersionReconcileConflict(layerId, objectId, type));
        }

        return conflicts.ToImmutable();
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

}
