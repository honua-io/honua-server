// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Postgres-backed durable storage for disconnected-sync conflict records (#1167).
/// </summary>
internal sealed class PostgresReplicaConflictRepository : IReplicaConflictRepository
{
    private const string SelectColumns = """
        conflict_id, replica_id, service_id, layer_id, objectid, conflict_type, status,
        sync_operation_id, device_id, user_id, server_generation,
        base_state_json, client_state_json, server_state_json, detected_at,
        resolution_action, resolved_by, resolved_at, resolved_server_generation,
        client_edit_applied, storage_layer_id, resolution_base_generation,
        write_committed, finalized, resolution_input_hash, client_edit_outcome_unknown,
        client_edit_superseded, pre_write_state_token, pre_write_row_absent
        """;

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;

    public PostgresReplicaConflictRepository(IAdoNetDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public bool SupportsConflictReview => true;

    public async Task UpsertAsync(ReplicaConflictRecord record, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO honua.replica_conflicts (
                conflict_id, replica_id, service_id, layer_id, objectid, conflict_type, status,
                sync_operation_id, device_id, user_id, server_generation,
                base_state_json, client_state_json, server_state_json, detected_at,
                resolution_action, resolved_by, resolved_at, resolved_server_generation,
                client_edit_applied, storage_layer_id, resolution_base_generation,
                write_committed, finalized, resolution_input_hash, client_edit_outcome_unknown,
                client_edit_superseded, pre_write_state_token, pre_write_row_absent)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20,
                    $21, $22, $23, $24, $25, $26, $27, $28, $29)
            ON CONFLICT (conflict_id) DO UPDATE SET
                status = EXCLUDED.status,
                conflict_type = EXCLUDED.conflict_type,
                base_state_json = EXCLUDED.base_state_json,
                client_state_json = EXCLUDED.client_state_json,
                server_state_json = EXCLUDED.server_state_json,
                resolution_action = EXCLUDED.resolution_action,
                resolved_by = EXCLUDED.resolved_by,
                resolved_at = EXCLUDED.resolved_at,
                resolved_server_generation = EXCLUDED.resolved_server_generation,
                client_edit_applied = EXCLUDED.client_edit_applied,
                storage_layer_id = EXCLUDED.storage_layer_id,
                resolution_base_generation = EXCLUDED.resolution_base_generation,
                write_committed = EXCLUDED.write_committed,
                finalized = EXCLUDED.finalized,
                resolution_input_hash = EXCLUDED.resolution_input_hash,
                client_edit_outcome_unknown = EXCLUDED.client_edit_outcome_unknown,
                client_edit_superseded = EXCLUDED.client_edit_superseded,
                pre_write_state_token = EXCLUDED.pre_write_state_token,
                pre_write_row_absent = EXCLUDED.pre_write_row_absent
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue(NpgsqlDbType.Text, record.ConflictId);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, record.ReplicaId);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, record.ServiceId);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, record.LayerId);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, record.ObjectId);
        command.Parameters.AddWithValue(NpgsqlDbType.Smallint, (short)record.ConflictType);
        command.Parameters.AddWithValue(NpgsqlDbType.Smallint, (short)record.Status);
        command.Parameters.Add(NullableText(record.SyncOperationId));
        command.Parameters.Add(NullableText(record.DeviceId));
        command.Parameters.Add(NullableText(record.UserId));
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, record.ServerGeneration);
        command.Parameters.Add(NullableJsonb(record.BaseStateJson));
        command.Parameters.Add(NullableJsonb(record.ClientStateJson));
        command.Parameters.Add(NullableJsonb(record.ServerStateJson));
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, record.DetectedAt);
        command.Parameters.Add(NullableSmallint((short?)record.ResolutionAction));
        command.Parameters.Add(NullableText(record.ResolvedBy));
        command.Parameters.Add(NullableTimestampTz(record.ResolvedAt));
        command.Parameters.Add(NullableBigint(record.ResolvedServerGeneration));
        command.Parameters.AddWithValue(NpgsqlDbType.Boolean, record.ClientEditApplied);
        command.Parameters.Add(NullableInteger(record.StorageLayerId));
        command.Parameters.Add(NullableBigint(record.ResolutionBaseGeneration));
        command.Parameters.AddWithValue(NpgsqlDbType.Boolean, record.WriteCommitted);
        command.Parameters.AddWithValue(NpgsqlDbType.Boolean, !record.FinalizationPending);
        command.Parameters.Add(NullableText(record.ResolutionInputHash));
        command.Parameters.AddWithValue(NpgsqlDbType.Boolean, record.ClientEditOutcomeUnknown);
        command.Parameters.AddWithValue(NpgsqlDbType.Boolean, record.ClientEditSuperseded);
        command.Parameters.Add(NullableText(record.PreWriteStateToken));
        command.Parameters.Add(NullableBoolean(record.PreWriteRowAbsent));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ReplicaConflictRecord>> ListByReplicaAsync(
        string replicaId,
        ReplicaConflictStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT {SelectColumns}
            FROM honua.replica_conflicts
            WHERE replica_id = $1
              AND ($2::smallint IS NULL OR status = $2)
            ORDER BY detected_at DESC, conflict_id ASC
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, replicaId);
        command.Parameters.Add(NullableSmallint(status is null ? null : (short)status.Value));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ReplicaConflictRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<ReplicaConflictRecord?> GetAsync(string conflictId, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT {SelectColumns}
            FROM honua.replica_conflicts
            WHERE conflict_id = $1
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, conflictId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return Map(reader);
    }

    public async Task<bool> TryUpdateDetectionStateAsync(
        ReplicaConflictDetectionUpdate update,
        CancellationToken cancellationToken = default)
    {
        // Only the detection-owned columns are touched, and only while the conflict is still pending:
        // detection post-processing can still be running when an operator resolves the freshly listed
        // conflict, and a full-record rewrite from a stale read would reopen that resolution (#2430).
        // COALESCE leaves a column unchanged when the caller passes null for it.
        var sql = $"""
            UPDATE honua.replica_conflicts
            SET conflict_type = COALESCE($2, conflict_type),
                client_state_json = COALESCE($3, client_state_json),
                server_state_json = CASE WHEN $9 THEN NULL ELSE COALESCE($4, server_state_json) END,
                client_edit_applied = COALESCE($5, client_edit_applied),
                client_edit_outcome_unknown = COALESCE($7, client_edit_outcome_unknown),
                client_edit_superseded = COALESCE($8, client_edit_superseded),
                resolution_base_generation = COALESCE($6, resolution_base_generation)
            WHERE conflict_id = $1
              AND status = {(short)ReplicaConflictStatus.Pending}
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, update.ConflictId);
        command.Parameters.Add(NullableSmallint((short?)update.ConflictType));
        command.Parameters.Add(NullableJsonb(update.ClientStateJson));
        command.Parameters.Add(NullableJsonb(update.ServerStateJson));
        command.Parameters.Add(NullableBoolean(update.ClientEditApplied));
        command.Parameters.Add(NullableBigint(update.ResolutionBaseGeneration));
        command.Parameters.Add(NullableBoolean(update.ClientEditOutcomeUnknown));
        command.Parameters.Add(NullableBoolean(update.ClientEditSuperseded));
        command.Parameters.AddWithValue(NpgsqlDbType.Boolean, update.ClearServerState);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> TryUpdateFinalizationStateAsync(
        ReplicaConflictFinalizationUpdate update,
        CancellationToken cancellationToken = default)
    {
        // Bound to the claim that produced this progress, not merely to "not pending". A deferral
        // leaves the row Deferred and can be superseded by a real resolution; without the
        // resolved_by/resolution_action guard the superseded request's late finalizer could mark the
        // NEWER resolution complete before its own write and audit finished, making a crash there
        // non-resumable (#2430).
        var sql = $"""
            UPDATE honua.replica_conflicts
            SET write_committed = COALESCE($2, write_committed),
                resolved_server_generation = COALESCE($3, resolved_server_generation),
                finalized = COALESCE($4, finalized),
                pre_write_state_token = COALESCE($8, pre_write_state_token),
                pre_write_row_absent = COALESCE($9, pre_write_row_absent)
            WHERE conflict_id = $1
              AND status <> {(short)ReplicaConflictStatus.Pending}
              AND resolved_by = $5
              AND resolution_action = $6
              AND resolved_at = $7
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, update.ConflictId);
        command.Parameters.Add(NullableBoolean(update.WriteCommitted));
        command.Parameters.Add(NullableBigint(update.ResolvedServerGeneration));
        command.Parameters.Add(NullableBoolean(update.Finalized));
        command.Parameters.AddWithValue(NpgsqlDbType.Text, update.ResolvedBy);
        command.Parameters.AddWithValue(NpgsqlDbType.Smallint, (short)update.Action);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, update.ResolvedAt);
        command.Parameters.Add(NullableText(update.PreWriteStateToken));
        command.Parameters.Add(NullableBoolean(update.PreWriteRowAbsent));

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> TryTakeOverClaimAsync(
        string conflictId,
        string resolvedBy,
        ReplicaConflictResolutionAction action,
        DateTimeOffset expectedResolvedAt,
        DateTimeOffset newResolvedAt,
        CancellationToken cancellationToken = default)
    {
        // Single-winner takeover of an unfinalized claim: bound to the old timestamp, so exactly one
        // of several concurrent recoveries proceeds and the losers see the row has moved on. Stamping
        // a new resolved_at also fences the previous holder, whose finalization and release are both
        // bound to the timestamp being replaced. A committed write does not block the takeover — the
        // resume path is what completes such a claim, and the same statement is used to back-date a
        // retained claim's lease so the operator's own retry can resume at once (#2430).
        const string sql = """
            UPDATE honua.replica_conflicts
            SET resolved_at = $5
            WHERE conflict_id = $1
              AND resolved_by = $2
              AND resolution_action = $3
              AND resolved_at = $4
              AND NOT finalized
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, conflictId);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, resolvedBy);
        command.Parameters.AddWithValue(NpgsqlDbType.Smallint, (short)action);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, expectedResolvedAt);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, newResolvedAt);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> TryReleaseClaimAsync(
        string conflictId,
        string resolvedBy,
        ReplicaConflictResolutionAction action,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default)
    {
        // Bound to the exact claim, including its timestamp, so a stale release cannot clear a
        // replacement claim taken after this one expired (#2430).
        var sql = $"""
            UPDATE honua.replica_conflicts
            SET status = {(short)ReplicaConflictStatus.Pending},
                resolution_action = NULL,
                resolved_by = NULL,
                resolved_at = NULL,
                resolved_server_generation = NULL,
                resolution_input_hash = NULL,
                write_committed = FALSE,
                pre_write_state_token = NULL,
                pre_write_row_absent = NULL,
                -- Back to TRUE: `finalized` means "no attempt in flight", and the claim guard requires
                -- it. Leaving it FALSE made a released row unclaimable AND unresumable — the release
                -- clears the actor/action identity a resume matches on — so every conflict released
                -- after a stale, cancelled, or failed resolution would have been bricked (#2430).
                finalized = TRUE
            WHERE conflict_id = $1
              AND resolved_by = $2
              AND resolution_action = $3
              AND resolved_at = $4
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, conflictId);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, resolvedBy);
        command.Parameters.AddWithValue(NpgsqlDbType.Smallint, (short)action);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, resolvedAt);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<ReplicaConflictResolutionOutcome> ResolveAsync(
        ReplicaConflictResolution resolution,
        CancellationToken cancellationToken = default)
    {
        // A Defer action keeps the conflict reviewable (non-terminal), so it is reapplyable. Any
        // other action is terminal and is only applied to a conflict that is still pending or
        // deferred — never one already resolved with a non-defer action. The WHERE guard makes the
        // transition atomic and the RETURNING clause surfaces the post-state for the caller.
        //
        // `AND finalized` is what makes the claim single-winner across deferrals (#2430): a deferred
        // row stays claimable, but only while no attempt is mid-flight on it. Without it two requests
        // could both claim the same Deferred row, and the loser's stale progress updates would be
        // silently dropped while it still reported success. A pending conflict is inserted finalized,
        // and the claim below clears the flag for the duration of the attempt.
        var newStatus = resolution.Action == ReplicaConflictResolutionAction.Defer
            ? (short)ReplicaConflictStatus.Deferred
            : (short)ReplicaConflictStatus.Resolved;

        var sql = $"""
            UPDATE honua.replica_conflicts
            SET status = $2,
                resolution_action = $3,
                resolved_by = $4,
                resolved_at = $5,
                resolved_server_generation = $6,
                resolution_input_hash = $7,
                write_committed = FALSE,
                pre_write_state_token = NULL,
                pre_write_row_absent = NULL,
                finalized = FALSE
            WHERE conflict_id = $1
              AND status <> {(short)ReplicaConflictStatus.Resolved}
              AND finalized
            RETURNING {SelectColumns}
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, resolution.ConflictId);
        command.Parameters.AddWithValue(NpgsqlDbType.Smallint, newStatus);
        command.Parameters.AddWithValue(NpgsqlDbType.Smallint, (short)resolution.Action);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, resolution.ResolvedBy);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, resolution.ResolvedAt);
        command.Parameters.Add(NullableBigint(resolution.ResolvedServerGeneration));
        command.Parameters.Add(NullableText(resolution.ResolutionInputHash));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // This call won the guarded update and applied the resolution.
            return new ReplicaConflictResolutionOutcome(Map(reader), Applied: true);
        }

        // No row updated: either the conflict does not exist or it was already resolved (possibly by
        // a concurrent caller). Re-read so the caller can distinguish not-found (null record) from
        // already-resolved (a terminal record with Applied == false).
        await reader.DisposeAsync().ConfigureAwait(false);
        var current = await GetAsync(resolution.ConflictId, cancellationToken).ConfigureAwait(false);
        return new ReplicaConflictResolutionOutcome(current, Applied: false);
    }

    private static ReplicaConflictRecord Map(NpgsqlDataReader reader) => new()
    {
        ConflictId = reader.GetString(0),
        ReplicaId = reader.GetString(1),
        ServiceId = reader.GetString(2),
        LayerId = reader.GetInt32(3),
        ObjectId = reader.GetInt64(4),
        ConflictType = (ReplicaConflictType)reader.GetInt16(5),
        Status = (ReplicaConflictStatus)reader.GetInt16(6),
        SyncOperationId = reader.IsDBNull(7) ? null : reader.GetString(7),
        DeviceId = reader.IsDBNull(8) ? null : reader.GetString(8),
        UserId = reader.IsDBNull(9) ? null : reader.GetString(9),
        ServerGeneration = reader.GetInt64(10),
        BaseStateJson = reader.IsDBNull(11) ? null : reader.GetString(11),
        ClientStateJson = reader.IsDBNull(12) ? null : reader.GetString(12),
        ServerStateJson = reader.IsDBNull(13) ? null : reader.GetString(13),
        DetectedAt = reader.GetFieldValue<DateTimeOffset>(14),
        ResolutionAction = reader.IsDBNull(15) ? null : (ReplicaConflictResolutionAction)reader.GetInt16(15),
        ResolvedBy = reader.IsDBNull(16) ? null : reader.GetString(16),
        ResolvedAt = reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17),
        ResolvedServerGeneration = reader.IsDBNull(18) ? null : reader.GetInt64(18),
        ClientEditApplied = reader.GetBoolean(19),
        StorageLayerId = reader.IsDBNull(20) ? null : reader.GetInt32(20),
        ResolutionBaseGeneration = reader.IsDBNull(21) ? null : reader.GetInt64(21),
        WriteCommitted = reader.GetBoolean(22),
        FinalizationPending = !reader.GetBoolean(23),
        ResolutionInputHash = reader.IsDBNull(24) ? null : reader.GetString(24),
        ClientEditOutcomeUnknown = reader.GetBoolean(25),
        ClientEditSuperseded = reader.GetBoolean(26),
        PreWriteStateToken = reader.IsDBNull(27) ? null : reader.GetString(27),
        PreWriteRowAbsent = reader.IsDBNull(28) ? null : reader.GetBoolean(28),
    };

    private static NpgsqlParameter NullableText(string? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter NullableJsonb(string? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter NullableSmallint(short? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Smallint, Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter NullableBigint(long? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter NullableInteger(int? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Integer, Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter NullableBoolean(bool? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Boolean, Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter NullableTimestampTz(DateTimeOffset? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)value ?? DBNull.Value };
}
