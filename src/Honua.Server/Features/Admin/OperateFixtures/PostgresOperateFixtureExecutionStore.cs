// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.ControlPlane;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Admin.OperateFixtures;

internal sealed partial class PostgresOperateFixtureExecutionStore(
    IServiceScopeFactory scopeFactory,
    ILogger<PostgresOperateFixtureExecutionStore> logger) : IExecutionJobStore, IExecutionLogStore
{
    private const string JobsTable = "honua.operate_fixture_execution_jobs";
    private const string LogsTable = "honua.operate_fixture_execution_logs";
    private const int MinQueryLimit = 1;
    private const int MaxJobQueryLimit = 200;
    private const int MaxLogQueryLimit = 500;
    private static readonly string[] _terminalStatuses = ["Succeeded", "Failed", "Cancelled"];

    public Task<bool> TryAcquireLeaseAsync(
        string operationId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public Task<bool> RenewLeaseAsync(
        string operationId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public Task ReleaseLeaseAsync(
        string operationId,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task<bool> TryCreateAsync(
        ExecutionJobRecord job,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var versioned = job with { Version = 1 };
        await using var lease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = lease.Connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {JobsTable} (
                operation_id, fixture_profile, record_json, version, status, kind, backend, queue,
                requested_by, correlation_id, trace_id, definition_id, resource_refs, environment,
                server, release_id, change_set_id, alert_id, created_at, updated_at, completed_at)
            VALUES (
                @operation_id, @fixture_profile, @record_json, @version, @status, @kind, @backend, @queue,
                @requested_by, @correlation_id, @trace_id, @definition_id, @resource_refs, @environment,
                @server, @release_id, @change_set_id, @alert_id, @created_at, @updated_at, @completed_at)
            ON CONFLICT (operation_id) DO NOTHING
            """;
        AddJobParameters(command, versioned);

        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows > 0)
        {
            ExecutionJobCreated(logger, versioned.OperationId, versioned.Status.ToString());
            return true;
        }

        return false;
    }

    public async Task<ExecutionJobRecord?> GetAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = lease.Connection.CreateCommand();
        command.CommandText = $"SELECT record_json FROM {JobsTable} WHERE operation_id = @operation_id";
        command.Parameters.Add(Parameter("operation_id", NpgsqlDbType.Text, operationId));

        var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return payload is string text
            ? JsonSerializer.Deserialize(text, ControlPlaneJsonContext.Default.ExecutionJobRecord)
            : null;
    }

    public async Task SetAsync(
        ExecutionJobRecord job,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var versioned = job with { Version = job.Version + 1 };
        await using var lease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = lease.Connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {JobsTable} (
                operation_id, fixture_profile, record_json, version, status, kind, backend, queue,
                requested_by, correlation_id, trace_id, definition_id, resource_refs, environment,
                server, release_id, change_set_id, alert_id, created_at, updated_at, completed_at)
            VALUES (
                @operation_id, @fixture_profile, @record_json, @version, @status, @kind, @backend, @queue,
                @requested_by, @correlation_id, @trace_id, @definition_id, @resource_refs, @environment,
                @server, @release_id, @change_set_id, @alert_id, @created_at, @updated_at, @completed_at)
            ON CONFLICT (operation_id) DO UPDATE SET
                fixture_profile = EXCLUDED.fixture_profile,
                record_json = EXCLUDED.record_json,
                version = EXCLUDED.version,
                status = EXCLUDED.status,
                kind = EXCLUDED.kind,
                backend = EXCLUDED.backend,
                queue = EXCLUDED.queue,
                requested_by = EXCLUDED.requested_by,
                correlation_id = EXCLUDED.correlation_id,
                trace_id = EXCLUDED.trace_id,
                definition_id = EXCLUDED.definition_id,
                resource_refs = EXCLUDED.resource_refs,
                environment = EXCLUDED.environment,
                server = EXCLUDED.server,
                release_id = EXCLUDED.release_id,
                change_set_id = EXCLUDED.change_set_id,
                alert_id = EXCLUDED.alert_id,
                created_at = EXCLUDED.created_at,
                updated_at = EXCLUDED.updated_at,
                completed_at = EXCLUDED.completed_at
            """;
        AddJobParameters(command, versioned);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        ExecutionJobUpdated(logger, versioned.OperationId, versioned.Status.ToString());
    }

    public async Task<bool> TrySetAsync(
        ExecutionJobRecord job,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var versioned = job with { Version = job.Version + 1 };
        await using var lease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = lease.Connection.CreateCommand();
        command.CommandText = $"""
            UPDATE {JobsTable}
            SET fixture_profile = @fixture_profile,
                record_json = @record_json,
                version = @version,
                status = @status,
                kind = @kind,
                backend = @backend,
                queue = @queue,
                requested_by = @requested_by,
                correlation_id = @correlation_id,
                trace_id = @trace_id,
                definition_id = @definition_id,
                resource_refs = @resource_refs,
                environment = @environment,
                server = @server,
                release_id = @release_id,
                change_set_id = @change_set_id,
                alert_id = @alert_id,
                created_at = @created_at,
                updated_at = @updated_at,
                completed_at = @completed_at
            WHERE operation_id = @operation_id AND version = @expected_version
            """;
        AddJobParameters(command, versioned);
        command.Parameters.Add(Parameter("expected_version", NpgsqlDbType.Bigint, job.Version));

        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 0)
        {
            CasConflict(logger, job.OperationId, job.Version);
            return false;
        }

        ExecutionJobUpdated(logger, versioned.OperationId, versioned.Status.ToString());
        return true;
    }

    public async Task<ExecutionJobPage> QueryAsync(
        ExecutionJobQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!TryDecodeJobCursor(query.Cursor, out var cursor, out var cursorError))
        {
            throw new ArgumentException(cursorError, nameof(query));
        }

        var limit = Math.Clamp(query.Limit, MinQueryLimit, MaxJobQueryLimit);
        // Select created_at alongside record_json so the keyset cursor is encoded from the
        // exact TIMESTAMPTZ column used by ORDER BY and the cursor predicate. Encoding from the
        // deserialized record_json instead can drift at the page boundary because PostgreSQL
        // microsecond precision differs from the serialized DateTimeOffset ticks.
        var sql = new StringBuilder($"SELECT record_json, created_at FROM {JobsTable} WHERE 1=1");
        await using var lease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = lease.Connection.CreateCommand();

        AppendJobFilters(sql, command, query, cursor);
        sql.Append(" ORDER BY created_at DESC, operation_id DESC LIMIT @limit");
        command.Parameters.Add(Parameter("limit", NpgsqlDbType.Integer, limit + 1));
        command.CommandText = sql.ToString();

        var rows = new List<ExecutionJobRecord>(limit + 1);
        var createdAts = new List<DateTimeOffset>(limit + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var job = JsonSerializer.Deserialize(
                reader.GetString(0),
                ControlPlaneJsonContext.Default.ExecutionJobRecord);
            if (job is not null)
            {
                rows.Add(job);
                createdAts.Add(reader.GetFieldValue<DateTimeOffset>(1));
            }
        }

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            var lastKept = rows[limit - 1];
            rows.RemoveAt(rows.Count - 1);
            nextCursor = EncodeJobCursor(createdAts[limit - 1], lastKept.OperationId);
        }

        return new ExecutionJobPage { Items = rows, NextCursor = nextCursor };
    }

    public async Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(
        ExecutionJobKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = lease.Connection.CreateCommand();
        var sql = new StringBuilder($"""
            SELECT record_json
            FROM {JobsTable}
            WHERE status <> ALL(@terminal_statuses)
            """);
        command.Parameters.Add(Parameter(
            "terminal_statuses",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            _terminalStatuses));

        if (kind.HasValue)
        {
            sql.Append(" AND kind = @kind");
            command.Parameters.Add(Parameter("kind", NpgsqlDbType.Text, kind.Value.ToString()));
        }

        sql.Append(" ORDER BY updated_at DESC, operation_id DESC");
        command.CommandText = sql.ToString();

        var rows = new List<ExecutionJobRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var job = JsonSerializer.Deserialize(
                reader.GetString(0),
                ControlPlaneJsonContext.Default.ExecutionJobRecord);
            if (job is not null)
            {
                rows.Add(job);
            }
        }

        return rows;
    }

    public async Task AppendAsync(
        string operationId,
        ExecutionLogEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(entry);

        await using var lease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = lease.Connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {LogsTable} (operation_id, fixture_profile, timestamp, level, payload_json)
            VALUES (@operation_id, @fixture_profile, @timestamp, @level, @payload_json)
            """;
        AddLogParameters(command, operationId, ResolveFixtureProfile(entry), entry);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        LogEntryAppended(logger, operationId, entry.Level.ToString());
    }

    public async Task<IReadOnlyList<ExecutionLogEntry>> GetLogsAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadLogsAsync(
            lease.Connection,
            $"SELECT payload_json FROM {LogsTable} WHERE operation_id = @operation_id ORDER BY log_id",
            operationId,
            null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecutionLogTail> TailAsync(
        string operationId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, MinQueryLimit, MaxLogQueryLimit);
        await using var lease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var countCommand = lease.Connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM {LogsTable} WHERE operation_id = @operation_id";
        countCommand.Parameters.Add(Parameter("operation_id", NpgsqlDbType.Text, operationId));
        var total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (total == 0)
        {
            return new ExecutionLogTail { Items = [], TotalCount = 0 };
        }

        var offset = Math.Max(0, total - boundedLimit);
        var entries = await ReadLogsAsync(
            lease.Connection,
            $"SELECT payload_json FROM {LogsTable} WHERE operation_id = @operation_id ORDER BY log_id OFFSET @offset LIMIT @limit",
            operationId,
            command =>
            {
                command.Parameters.Add(Parameter("offset", NpgsqlDbType.Integer, offset));
                command.Parameters.Add(Parameter("limit", NpgsqlDbType.Integer, boundedLimit));
            },
            cancellationToken).ConfigureAwait(false);

        return new ExecutionLogTail { Items = entries, TotalCount = total };
    }

    public async Task<ExecutionLogPage> QueryAsync(
        string operationId,
        ExecutionLogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!TryDecodeOffsetCursor(query.Cursor, out var offset, out var cursorError))
        {
            throw new ArgumentException(cursorError, nameof(query));
        }

        var limit = Math.Clamp(query.Limit, MinQueryLimit, MaxLogQueryLimit);
        await using var lease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var entries = await ReadLogsAsync(
            lease.Connection,
            $"SELECT payload_json FROM {LogsTable} WHERE operation_id = @operation_id ORDER BY log_id OFFSET @offset LIMIT @limit",
            operationId,
            command =>
            {
                command.Parameters.Add(Parameter("offset", NpgsqlDbType.Integer, offset));
                command.Parameters.Add(Parameter("limit", NpgsqlDbType.Integer, limit + 1));
            },
            cancellationToken).ConfigureAwait(false);

        string? nextCursor = null;
        if (entries.Count > limit)
        {
            entries.RemoveAt(entries.Count - 1);
            nextCursor = EncodeOffsetCursor(offset + limit);
        }

        return new ExecutionLogPage { Items = entries, NextCursor = nextCursor };
    }

    public Task SetRetentionAsync(
        string operationId,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task ReplaceLogsAsync(
        string operationId,
        string profile,
        IReadOnlyList<ExecutionLogEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        ArgumentNullException.ThrowIfNull(entries);

        await using var lease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await lease.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var delete = lease.Connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {LogsTable} WHERE operation_id = @operation_id AND fixture_profile = @fixture_profile";
            delete.Parameters.Add(Parameter("operation_id", NpgsqlDbType.Text, operationId));
            delete.Parameters.Add(Parameter("fixture_profile", NpgsqlDbType.Text, profile));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var entry in entries)
        {
            await using var insert = lease.Connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"""
                INSERT INTO {LogsTable} (operation_id, fixture_profile, timestamp, level, payload_json)
                VALUES (@operation_id, @fixture_profile, @timestamp, @level, @payload_json)
                """;
            AddLogParameters(insert, operationId, profile, entry);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ScopedConnectionLease> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateScope();
        try
        {
            var connectionProvider = scope.ServiceProvider.GetRequiredService<IDatabaseConnectionProvider>();
            var connection = await connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return new ScopedConnectionLease(scope, connection);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    private static void AppendJobFilters(
        StringBuilder sql,
        DbCommand command,
        ExecutionJobQuery query,
        JobCursor? cursor)
    {
        if (query.Statuses.Count > 0)
        {
            sql.Append(" AND status = ANY(@statuses)");
            command.Parameters.Add(Parameter(
                "statuses",
                NpgsqlDbType.Array | NpgsqlDbType.Text,
                query.Statuses.Select(status => status.ToString()).ToArray()));
        }

        if (query.Kind.HasValue)
        {
            sql.Append(" AND kind = @kind");
            command.Parameters.Add(Parameter("kind", NpgsqlDbType.Text, query.Kind.Value.ToString()));
        }

        AddExactFilter(sql, command, "backend", query.Backend);
        AddExactFilter(sql, command, "queue", query.Queue);
        AddExactFilter(sql, command, "requested_by", query.RequestedBy);
        AddExactFilter(sql, command, "correlation_id", query.CorrelationId);
        AddExactFilter(sql, command, "trace_id", query.TraceId);
        // Mirror RedisExecutionJobStore.MatchesDefinition: match the definition id against both
        // the DefinitionId and GeoprocessingPlanId parameters, case-insensitively. The
        // definition_id column stores DefinitionId ?? GeoprocessingPlanId, so the plan id is also
        // matched directly from record_json when a distinct DefinitionId is present.
        if (!string.IsNullOrWhiteSpace(query.DefinitionId))
        {
            sql.Append(" AND (lower(definition_id) = lower(@definition_id)")
               .Append(" OR lower(record_json -> 'spec' -> 'parameters' ->> @plan_id_key) = lower(@definition_id))");
            command.Parameters.Add(Parameter("definition_id", NpgsqlDbType.Text, query.DefinitionId.Trim()));
            command.Parameters.Add(Parameter("plan_id_key", NpgsqlDbType.Text, ExecutionJobParameterKeys.GeoprocessingPlanId));
        }

        AddExactFilter(sql, command, "environment", query.Environment);
        AddExactFilter(sql, command, "server", query.Server);
        AddExactFilter(sql, command, "release_id", query.ReleaseId);
        AddExactFilter(sql, command, "change_set_id", query.ChangeSetId);
        AddExactFilter(sql, command, "alert_id", query.AlertId);

        if (!string.IsNullOrWhiteSpace(query.ResourceRef))
        {
            // Case-insensitive membership to mirror RedisExecutionJobStore.MatchesAny, which
            // compares resource refs with OrdinalIgnoreCase rather than the exact ANY(...) match.
            sql.Append(" AND EXISTS (SELECT 1 FROM unnest(resource_refs) AS rref WHERE lower(rref) = lower(@resource_ref))");
            command.Parameters.Add(Parameter("resource_ref", NpgsqlDbType.Text, query.ResourceRef.Trim()));
        }

        if (query.CreatedFrom is { } from)
        {
            sql.Append(" AND created_at >= @created_from");
            command.Parameters.Add(Parameter("created_from", NpgsqlDbType.TimestampTz, from));
        }

        if (query.CreatedTo is { } to)
        {
            sql.Append(" AND created_at < @created_to");
            command.Parameters.Add(Parameter("created_to", NpgsqlDbType.TimestampTz, to));
        }

        if (cursor.HasValue)
        {
            sql.Append(" AND (created_at < @cursor_created_at OR (created_at = @cursor_created_at AND operation_id < @cursor_operation_id))");
            command.Parameters.Add(Parameter("cursor_created_at", NpgsqlDbType.TimestampTz, cursor.Value.CreatedAt));
            command.Parameters.Add(Parameter("cursor_operation_id", NpgsqlDbType.Text, cursor.Value.JobId));
        }
    }

    private static void AddExactFilter(StringBuilder sql, DbCommand command, string column, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var parameterName = column;
        sql.Append(" AND lower(").Append(column).Append(") = lower(@").Append(parameterName).Append(')');
        command.Parameters.Add(Parameter(parameterName, NpgsqlDbType.Text, value.Trim()));
    }

    private static async Task<List<ExecutionLogEntry>> ReadLogsAsync(
        DbConnection connection,
        string sql,
        string operationId,
        Action<DbCommand>? configure,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(Parameter("operation_id", NpgsqlDbType.Text, operationId));
        configure?.Invoke(command);

        var entries = new List<ExecutionLogEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var entry = JsonSerializer.Deserialize(
                reader.GetString(0),
                ControlPlaneJsonContext.Default.ExecutionLogEntry);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static void AddJobParameters(DbCommand command, ExecutionJobRecord job)
    {
        var parameters = job.Spec.Parameters;
        command.Parameters.Add(Parameter("operation_id", NpgsqlDbType.Text, job.OperationId));
        command.Parameters.Add(Parameter("fixture_profile", NpgsqlDbType.Text, ResolveFixtureProfile(job)));
        command.Parameters.Add(Parameter(
            "record_json",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(job, ControlPlaneJsonContext.Default.ExecutionJobRecord)));
        command.Parameters.Add(Parameter("version", NpgsqlDbType.Bigint, job.Version));
        command.Parameters.Add(Parameter("status", NpgsqlDbType.Text, job.Status.ToString()));
        command.Parameters.Add(Parameter("kind", NpgsqlDbType.Text, job.Spec.Kind.ToString()));
        command.Parameters.Add(Parameter("backend", NpgsqlDbType.Text, job.Spec.Backend));
        command.Parameters.Add(Parameter("queue", NpgsqlDbType.Text, ExecutionJobMetadata.ResolveQueue(job)));
        command.Parameters.Add(Parameter("requested_by", NpgsqlDbType.Text, job.Audit.RequestedBy));
        command.Parameters.Add(Parameter("correlation_id", NpgsqlDbType.Text, job.Audit.CorrelationId));
        command.Parameters.Add(Parameter("trace_id", NpgsqlDbType.Text, GetParameter(parameters, ExecutionJobParameterKeys.TraceId)));
        command.Parameters.Add(Parameter("definition_id", NpgsqlDbType.Text, ResolveDefinitionId(parameters)));
        command.Parameters.Add(Parameter(
            "resource_refs",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            ResolveResourceRefs(parameters)));
        command.Parameters.Add(Parameter("environment", NpgsqlDbType.Text, GetParameter(parameters, ExecutionJobParameterKeys.Environment)));
        command.Parameters.Add(Parameter("server", NpgsqlDbType.Text, GetParameter(parameters, ExecutionJobParameterKeys.Server)));
        command.Parameters.Add(Parameter("release_id", NpgsqlDbType.Text, GetParameter(parameters, ExecutionJobParameterKeys.ReleaseId)));
        command.Parameters.Add(Parameter("change_set_id", NpgsqlDbType.Text, GetParameter(parameters, ExecutionJobParameterKeys.ChangeSetId)));
        command.Parameters.Add(Parameter("alert_id", NpgsqlDbType.Text, GetParameter(parameters, ExecutionJobParameterKeys.AlertId)));
        command.Parameters.Add(Parameter("created_at", NpgsqlDbType.TimestampTz, job.CreatedAt));
        command.Parameters.Add(Parameter("updated_at", NpgsqlDbType.TimestampTz, job.UpdatedAt));
        command.Parameters.Add(Parameter("completed_at", NpgsqlDbType.TimestampTz, job.CompletedAt));
    }

    private static void AddLogParameters(
        DbCommand command,
        string operationId,
        string profile,
        ExecutionLogEntry entry)
    {
        command.Parameters.Add(Parameter("operation_id", NpgsqlDbType.Text, operationId));
        command.Parameters.Add(Parameter("fixture_profile", NpgsqlDbType.Text, profile));
        command.Parameters.Add(Parameter("timestamp", NpgsqlDbType.TimestampTz, entry.Timestamp));
        command.Parameters.Add(Parameter("level", NpgsqlDbType.Text, entry.Level.ToString()));
        command.Parameters.Add(Parameter(
            "payload_json",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(entry, ControlPlaneJsonContext.Default.ExecutionLogEntry)));
    }

    private static string ResolveFixtureProfile(ExecutionJobRecord job)
        => job.Spec.Parameters.TryGetValue(OperateObservabilityFixtureConstants.FixtureProfileParameter, out var profile) &&
           !string.IsNullOrWhiteSpace(profile)
            ? profile
            : "external";

    private static string ResolveFixtureProfile(ExecutionLogEntry entry)
        => entry.Metadata is not null &&
           entry.Metadata.TryGetValue(OperateObservabilityFixtureConstants.FixtureProfileParameter, out var profile) &&
           !string.IsNullOrWhiteSpace(profile)
            ? profile
            : "external";

    private static string? ResolveDefinitionId(IReadOnlyDictionary<string, string> parameters)
        => GetParameter(parameters, ExecutionJobParameterKeys.DefinitionId)
            ?? GetParameter(parameters, ExecutionJobParameterKeys.GeoprocessingPlanId);

    private static string[] ResolveResourceRefs(IReadOnlyDictionary<string, string> parameters)
    {
        var value = GetParameter(parameters, ExecutionJobParameterKeys.ResourceRefs);
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split(
                ExecutionJobParameterKeys.MetadataListSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? GetParameter(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static NpgsqlParameter Parameter(string name, NpgsqlDbType type, object? value)
        => new(name, type) { Value = value ?? DBNull.Value };

    private static bool TryDecodeJobCursor(string? cursor, out JobCursor? value, out string error)
    {
        value = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var separator = decoded.IndexOf('|', StringComparison.Ordinal);
            if (separator <= 0 || separator == decoded.Length - 1)
            {
                error = "Invalid job cursor.";
                return false;
            }

            if (!long.TryParse(decoded[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
            {
                error = "Invalid job cursor.";
                return false;
            }

            value = new JobCursor(new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc)), decoded[(separator + 1)..]);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            error = "Invalid job cursor.";
            return false;
        }
        catch (FormatException)
        {
            error = "Invalid job cursor.";
            return false;
        }
    }

    private static string EncodeJobCursor(DateTimeOffset createdAt, string jobId)
    {
        var value = string.Concat(createdAt.UtcTicks.ToString(CultureInfo.InvariantCulture), "|", jobId);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static bool TryDecodeOffsetCursor(string? cursor, out int offset, out string error)
    {
        offset = 0;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (!int.TryParse(decoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) || offset < 0)
            {
                error = "Invalid log cursor.";
                return false;
            }

            return true;
        }
        catch (FormatException)
        {
            error = "Invalid log cursor.";
            return false;
        }
    }

    private static string EncodeOffsetCursor(int offset)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));

    private sealed class ScopedConnectionLease(IServiceScope scope, DbConnection connection) : IAsyncDisposable
    {
        public DbConnection Connection { get; } = connection;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Connection.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                scope.Dispose();
            }
        }
    }

    private readonly record struct JobCursor(DateTimeOffset CreatedAt, string JobId);

    [LoggerMessage(120902, LogLevel.Information, "Created fixture execution job {OperationId} with status {Status}.")]
    private static partial void ExecutionJobCreated(ILogger logger, string operationId, string status);

    [LoggerMessage(120903, LogLevel.Debug, "Updated fixture execution job {OperationId} to status {Status}.")]
    private static partial void ExecutionJobUpdated(ILogger logger, string operationId, string status);

    [LoggerMessage(120904, LogLevel.Debug, "Fixture execution job CAS conflict for {OperationId} at version {Version}.")]
    private static partial void CasConflict(ILogger logger, string operationId, long version);

    [LoggerMessage(120905, LogLevel.Debug, "Fixture execution log entry appended: {OperationId}, Level={Level}.")]
    private static partial void LogEntryAppended(ILogger logger, string operationId, string level);
}
