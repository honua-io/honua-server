// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Observability;

/// <summary>
/// PostgreSQL implementation of <see cref="IOpsAutonomyPolicyStore"/> backed by
/// <c>honua.ops_autonomy_*</c> tables.
/// </summary>
internal sealed class PostgresOpsAutonomyPolicyStore : IOpsAutonomyPolicyStore
{
    private const string SettingsId = "global";

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly IAuditLog? _auditLog;
    private readonly string _policyTable;
    private readonly string _settingsTable;
    private readonly string _trackTable;
    private readonly string _actionTable;

    public PostgresOpsAutonomyPolicyStore(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        IAuditLog? auditLog = null,
        string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _auditLog = auditLog;
        _policyTable = SchemaSearchPath.QualifyTable("ops_autonomy_policies", schemaName);
        _settingsTable = SchemaSearchPath.QualifyTable("ops_autonomy_settings", schemaName);
        _trackTable = SchemaSearchPath.QualifyTable("ops_autonomy_rule_track_records", schemaName);
        _actionTable = SchemaSearchPath.QualifyTable("ops_autonomy_action_log", schemaName);
    }

    public async Task<OpsAutonomyPolicy?> GetPolicyAsync(string rule, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rule);

        var sql = $"""
            SELECT rule, mode, max_auto_actions_per_window, window_seconds, max_blast_radius, updated_at, updated_by
            FROM {_policyTable}
            WHERE rule = @rule
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("rule", NpgsqlDbType.Text, rule);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadPolicy(reader)
            : null;
    }

    public async Task<IReadOnlyList<OpsAutonomyPolicySnapshot>> ListPoliciesAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT p.rule, p.mode, p.max_auto_actions_per_window, p.window_seconds, p.max_blast_radius, p.updated_at, p.updated_by,
                   COALESCE(t.proposals_raised, 0), COALESCE(t.proposals_approved, 0), COALESCE(t.proposals_rejected, 0),
                   COALESCE(t.auto_applied, 0), COALESCE(t.rolled_back, 0), COALESCE(t.failed, 0),
                   t.first_activity_at, t.last_activity_at
            FROM {_policyTable} p
            LEFT JOIN {_trackTable} t ON t.rule = p.rule
            ORDER BY p.rule
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<OpsAutonomyPolicySnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new OpsAutonomyPolicySnapshot
            {
                Policy = ReadPolicy(reader),
                TrackRecord = ReadTrack(reader, offset: 7),
            });
        }

        return results;
    }

    public async Task<OpsAutonomyPolicySnapshot> SetPolicyAsync(
        OpsAutonomyPolicy policy,
        string changedBy,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.Rule);

        var normalized = Normalize(policy);
        var now = DateTimeOffset.UtcNow;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var sql = $"""
            INSERT INTO {_policyTable}
                (rule, mode, max_auto_actions_per_window, window_seconds, max_blast_radius, updated_at, updated_by)
            VALUES
                (@rule, @mode, @max_auto_actions_per_window, @window_seconds, @max_blast_radius, @updated_at, @updated_by)
            ON CONFLICT (rule) DO UPDATE SET
                mode = EXCLUDED.mode,
                max_auto_actions_per_window = EXCLUDED.max_auto_actions_per_window,
                window_seconds = EXCLUDED.window_seconds,
                max_blast_radius = EXCLUDED.max_blast_radius,
                updated_at = EXCLUDED.updated_at,
                updated_by = EXCLUDED.updated_by
            """;

        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            BindPolicy(command, normalized, changedBy, now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await TouchTrackAsync(connection, transaction, normalized.Rule, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);

        await RecordAuditAsync(
                "ops_autonomy.policy.update",
                changedBy,
                normalized.Rule,
                reason,
                $"{{\"rule\":\"{JsonEscape(normalized.Rule)}\",\"mode\":\"{normalized.Mode}\"}}",
                cancellationToken)
            .ConfigureAwait(false);

        return await ReadSnapshotAsync(normalized.Rule, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OpsAutonomySettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT kill_switch_enabled, updated_at, updated_by
            FROM {_settingsTable}
            WHERE settings_id = @settings_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("settings_id", NpgsqlDbType.Text, SettingsId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new OpsAutonomySettings();
        }

        return new OpsAutonomySettings
        {
            KillSwitchEnabled = reader.GetBoolean(0),
            UpdatedAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            UpdatedBy = reader.IsDBNull(2) ? null : reader.GetString(2),
        };
    }

    public async Task<OpsAutonomySettings> SetSettingsAsync(
        OpsAutonomySettings settings,
        string changedBy,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var actor = string.IsNullOrWhiteSpace(changedBy) ? AuditEvent.AnonymousActor : changedBy;
        var sql = $"""
            INSERT INTO {_settingsTable} (settings_id, kill_switch_enabled, updated_at, updated_by)
            VALUES (@settings_id, @kill_switch_enabled, @updated_at, @updated_by)
            ON CONFLICT (settings_id) DO UPDATE SET
                kill_switch_enabled = EXCLUDED.kill_switch_enabled,
                updated_at = EXCLUDED.updated_at,
                updated_by = EXCLUDED.updated_by
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("settings_id", NpgsqlDbType.Text, SettingsId);
        command.Parameters.AddWithValue("kill_switch_enabled", NpgsqlDbType.Boolean, settings.KillSwitchEnabled);
        command.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, now);
        command.Parameters.AddWithValue("updated_by", NpgsqlDbType.Text, actor);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await RecordAuditAsync(
                "ops_autonomy.settings.update",
                actor,
                SettingsId,
                reason,
                $"{{\"killSwitchEnabled\":{settings.KillSwitchEnabled.ToString().ToLowerInvariant()}}}",
                cancellationToken)
            .ConfigureAwait(false);

        return new OpsAutonomySettings
        {
            KillSwitchEnabled = settings.KillSwitchEnabled,
            UpdatedAt = now,
            UpdatedBy = actor,
        };
    }

    public async Task<OpsAutonomyReservationResult> TryReserveAutoActionAsync(
        OpsAutonomyReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FindingId);

        var now = DateTimeOffset.UtcNow;
        var cutoff = now - NormalizeWindow(request.Window);
        var maxActions = Math.Max(1, request.MaxAutoActionsPerWindow);
        var reservationId = $"auto-{Guid.NewGuid():N}";

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@rule))", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("rule", NpgsqlDbType.Text, request.Rule);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var existingSql = $"""
            SELECT 1
            FROM {_actionTable}
            WHERE finding_id = @finding_id
            LIMIT 1
            """;
        await using (var existingCommand = new NpgsqlCommand(existingSql, connection, transaction))
        {
            existingCommand.Parameters.AddWithValue("finding_id", NpgsqlDbType.Text, request.FindingId);
            var existing = await existingCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new OpsAutonomyReservationResult { Reserved = false, Reason = "finding-already-reserved" };
            }
        }

        var countSql = $"""
            SELECT COUNT(*)
            FROM {_actionTable}
            WHERE rule = @rule AND reserved_at >= @cutoff
            """;
        await using (var countCommand = new NpgsqlCommand(countSql, connection, transaction))
        {
            countCommand.Parameters.AddWithValue("rule", NpgsqlDbType.Text, request.Rule);
            countCommand.Parameters.AddWithValue("cutoff", NpgsqlDbType.TimestampTz, cutoff);
            var count = (long)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
            if (count >= maxActions)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new OpsAutonomyReservationResult { Reserved = false, Reason = "rate-limit-exceeded" };
            }
        }

        var insertSql = $"""
            INSERT INTO {_actionTable}
                (action_id, finding_id, rule, operation_class, action_discriminator, blast_radius, reserved_at)
            VALUES
                (@action_id, @finding_id, @rule, @operation_class, @action_discriminator, @blast_radius, @reserved_at)
            """;
        await using (var insertCommand = new NpgsqlCommand(insertSql, connection, transaction))
        {
            insertCommand.Parameters.AddWithValue("action_id", NpgsqlDbType.Text, reservationId);
            insertCommand.Parameters.AddWithValue("finding_id", NpgsqlDbType.Text, request.FindingId);
            insertCommand.Parameters.AddWithValue("rule", NpgsqlDbType.Text, request.Rule);
            insertCommand.Parameters.AddWithValue("operation_class", NpgsqlDbType.Text, request.OperationClass.ToString());
            insertCommand.Parameters.AddWithValue("action_discriminator", NpgsqlDbType.Text, (object?)request.ActionDiscriminator ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("blast_radius", NpgsqlDbType.Integer, Math.Max(1, request.BlastRadius));
            insertCommand.Parameters.AddWithValue("reserved_at", NpgsqlDbType.TimestampTz, now);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await TouchTrackAsync(connection, transaction, request.Rule, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return new OpsAutonomyReservationResult { Reserved = true, ReservationId = reservationId };
    }

    public async Task RecordAutoActionOutcomeAsync(
        string reservationId,
        OpsAutonomyActionOutcome outcome,
        string? operationId = null,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);

        var now = DateTimeOffset.UtcNow;
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        string? rule;
        var updateSql = $"""
            UPDATE {_actionTable}
            SET outcome = @outcome,
                execution_operation_id = @operation_id,
                outcome_message = @message,
                completed_at = @completed_at
            WHERE action_id = @action_id AND outcome IS NULL
            RETURNING rule
            """;
        await using (var updateCommand = new NpgsqlCommand(updateSql, connection, transaction))
        {
            updateCommand.Parameters.AddWithValue("action_id", NpgsqlDbType.Text, reservationId);
            updateCommand.Parameters.AddWithValue("outcome", NpgsqlDbType.Smallint, (short)outcome);
            updateCommand.Parameters.AddWithValue("operation_id", NpgsqlDbType.Text, (object?)operationId ?? DBNull.Value);
            updateCommand.Parameters.AddWithValue("message", NpgsqlDbType.Text, (object?)message ?? DBNull.Value);
            updateCommand.Parameters.AddWithValue("completed_at", NpgsqlDbType.TimestampTz, now);
            var returned = await updateCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            rule = returned is null or DBNull ? null : (string)returned;
        }

        if (rule is not null)
        {
            await IncrementOutcomeTrackAsync(connection, transaction, rule, outcome, now, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task IncrementProposalRaisedAsync(string rule, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rule);

        var now = DateTimeOffset.UtcNow;
        var sql = $"""
            INSERT INTO {_trackTable} (rule, proposals_raised, first_activity_at, last_activity_at)
            VALUES (@rule, 1, @now, @now)
            ON CONFLICT (rule) DO UPDATE SET
                proposals_raised = {_trackTable}.proposals_raised + 1,
                first_activity_at = COALESCE({_trackTable}.first_activity_at, EXCLUDED.first_activity_at),
                last_activity_at = EXCLUDED.last_activity_at
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("rule", NpgsqlDbType.Text, rule);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<OpsAutonomyPolicySnapshot> ReadSnapshotAsync(string rule, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT p.rule, p.mode, p.max_auto_actions_per_window, p.window_seconds, p.max_blast_radius, p.updated_at, p.updated_by,
                   COALESCE(t.proposals_raised, 0), COALESCE(t.proposals_approved, 0), COALESCE(t.proposals_rejected, 0),
                   COALESCE(t.auto_applied, 0), COALESCE(t.rolled_back, 0), COALESCE(t.failed, 0),
                   t.first_activity_at, t.last_activity_at
            FROM {_policyTable} p
            LEFT JOIN {_trackTable} t ON t.rule = p.rule
            WHERE p.rule = @rule
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("rule", NpgsqlDbType.Text, rule);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Autonomy policy '{rule}' was not persisted.");
        }

        return new OpsAutonomyPolicySnapshot
        {
            Policy = ReadPolicy(reader),
            TrackRecord = ReadTrack(reader, offset: 7),
        };
    }

    private async Task TouchTrackAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string rule,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_trackTable} (rule, first_activity_at, last_activity_at)
            VALUES (@rule, @now, @now)
            ON CONFLICT (rule) DO UPDATE SET
                first_activity_at = COALESCE({_trackTable}.first_activity_at, EXCLUDED.first_activity_at),
                last_activity_at = EXCLUDED.last_activity_at
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("rule", NpgsqlDbType.Text, rule);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task IncrementOutcomeTrackAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string rule,
        OpsAutonomyActionOutcome outcome,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var autoApplied = outcome == OpsAutonomyActionOutcome.Succeeded ? 1 : 0;
        var rolledBack = outcome == OpsAutonomyActionOutcome.RolledBack ? 1 : 0;
        // Indeterminate and post-invocation cancellation are intentionally counted in the
        // failed track-record bucket: neither may inflate the autonomous-success rate used
        // to justify policy graduation.
        var failed = outcome is OpsAutonomyActionOutcome.Failed
            or OpsAutonomyActionOutcome.Indeterminate
            or OpsAutonomyActionOutcome.Canceled
            ? 1
            : 0;
        var sql = $"""
            INSERT INTO {_trackTable}
                (rule, auto_applied, rolled_back, failed, first_activity_at, last_activity_at)
            VALUES
                (@rule, @auto_applied, @rolled_back, @failed, @now, @now)
            ON CONFLICT (rule) DO UPDATE SET
                auto_applied = {_trackTable}.auto_applied + EXCLUDED.auto_applied,
                rolled_back = {_trackTable}.rolled_back + EXCLUDED.rolled_back,
                failed = {_trackTable}.failed + EXCLUDED.failed,
                first_activity_at = COALESCE({_trackTable}.first_activity_at, EXCLUDED.first_activity_at),
                last_activity_at = EXCLUDED.last_activity_at
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("rule", NpgsqlDbType.Text, rule);
        command.Parameters.AddWithValue("auto_applied", NpgsqlDbType.Bigint, autoApplied);
        command.Parameters.AddWithValue("rolled_back", NpgsqlDbType.Bigint, rolledBack);
        command.Parameters.AddWithValue("failed", NpgsqlDbType.Bigint, failed);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task RecordAuditAsync(
        string action,
        string actor,
        string resourceId,
        string? reason,
        string details,
        CancellationToken cancellationToken)
    {
        if (_auditLog is null)
        {
            return Task.CompletedTask;
        }

        var resolvedActor = string.IsNullOrWhiteSpace(actor) ? AuditEvent.AnonymousActor : actor;
        var fullDetails = string.IsNullOrWhiteSpace(reason)
            ? details
            : details[..^1] + ",\"reason\":\"" + JsonEscape(reason) + "\"}";
        return _auditLog.RecordAsync(
            new AuditEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                EventType = AuditEventType.ConfigChange,
                Actor = resolvedActor,
                ActorType = resolvedActor == AuditEvent.AnonymousActor ? AuditActorType.Anonymous : AuditActorType.UserId,
                ResourceType = "ops_autonomy_policy",
                ResourceId = resourceId,
                Action = action,
                Outcome = AuditOutcome.Success,
                CorrelationId = resourceId,
                Details = fullDetails,
            },
            cancellationToken);
    }

    private static OpsAutonomyPolicy ReadPolicy(NpgsqlDataReader reader)
        => Normalize(new OpsAutonomyPolicy
        {
            Rule = reader.GetString(0),
            Mode = Enum.IsDefined((OpsAutonomyMode)reader.GetInt16(1))
                ? (OpsAutonomyMode)reader.GetInt16(1)
                : OpsAutonomyMode.ProposeOnly,
            MaxAutoActionsPerWindow = reader.GetInt32(2),
            Window = TimeSpan.FromSeconds(reader.GetInt32(3)),
            MaxBlastRadius = reader.GetInt32(4),
            UpdatedAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            UpdatedBy = reader.IsDBNull(6) ? null : reader.GetString(6),
        });

    private static OpsAutonomyTrackRecord ReadTrack(NpgsqlDataReader reader, int offset)
        => new()
        {
            Rule = reader.GetString(0),
            ProposalsRaised = reader.GetInt64(offset),
            ProposalsApproved = reader.GetInt64(offset + 1),
            ProposalsRejected = reader.GetInt64(offset + 2),
            AutoApplied = reader.GetInt64(offset + 3),
            RolledBack = reader.GetInt64(offset + 4),
            Failed = reader.GetInt64(offset + 5),
            FirstActivityAt = reader.IsDBNull(offset + 6) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 6),
            LastActivityAt = reader.IsDBNull(offset + 7) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 7),
        };

    private static void BindPolicy(NpgsqlCommand command, OpsAutonomyPolicy policy, string changedBy, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("rule", NpgsqlDbType.Text, policy.Rule);
        command.Parameters.AddWithValue("mode", NpgsqlDbType.Smallint, (short)policy.Mode);
        command.Parameters.AddWithValue("max_auto_actions_per_window", NpgsqlDbType.Integer, policy.MaxAutoActionsPerWindow);
        command.Parameters.AddWithValue("window_seconds", NpgsqlDbType.Integer, (int)Math.Ceiling(policy.Window.TotalSeconds));
        command.Parameters.AddWithValue("max_blast_radius", NpgsqlDbType.Integer, policy.MaxBlastRadius);
        command.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, now);
        command.Parameters.AddWithValue("updated_by", NpgsqlDbType.Text, string.IsNullOrWhiteSpace(changedBy) ? AuditEvent.AnonymousActor : changedBy);
    }

    private static OpsAutonomyPolicy Normalize(OpsAutonomyPolicy policy)
        => policy with
        {
            MaxAutoActionsPerWindow = Math.Max(1, policy.MaxAutoActionsPerWindow),
            Window = NormalizeWindow(policy.Window),
            MaxBlastRadius = Math.Max(1, policy.MaxBlastRadius),
        };

    private static TimeSpan NormalizeWindow(TimeSpan window)
        => window <= TimeSpan.Zero ? TimeSpan.FromHours(1) : window;

    private static string JsonEscape(string? value)
        => (value ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
