// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Share.Abstractions;
using Honua.Core.Features.Share.Domain;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Share;

internal sealed class PostgresShareExportStore : IShareExportStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _definitionsTable;
    private readonly string _runsTable;

    public PostgresShareExportStore(IDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
        _definitionsTable = SchemaSearchPath.QualifyTable("share_export_definitions", schemaName);
        _runsTable = SchemaSearchPath.QualifyTable("share_export_runs", schemaName);
    }

    public Task<ShareExportDefinitionPage> ListDefinitionsAsync(
        ShareExportDefinitionQuery query,
        CancellationToken cancellationToken = default)
        => TranslateStoreFailuresAsync(async () =>
        {
            var limit = Math.Clamp(query.Limit, 1, 200);
            var sql = new StringBuilder($"""
                SELECT export_id, resource_id, service_name, layer_id, display_name,
                       destination_type, destination_status, destination_config, format,
                       schedule, schedule_state, created_at, updated_at, last_run_at, next_run_at
                FROM {_definitionsTable}
                WHERE 1 = 1
                """);
            sql.AppendLine();

            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand { Connection = connection.Connection };

            if (!string.IsNullOrWhiteSpace(query.ServiceName))
            {
                sql.AppendLine("AND service_name = @service_name");
                command.Parameters.AddWithValue("@service_name", query.ServiceName);
            }

            if (!string.IsNullOrWhiteSpace(query.ResourceId))
            {
                sql.AppendLine("AND resource_id = @resource_id");
                command.Parameters.AddWithValue("@resource_id", query.ResourceId);
            }

            if (query.LayerId.HasValue)
            {
                sql.AppendLine("AND layer_id = @layer_id");
                command.Parameters.AddWithValue("@layer_id", query.LayerId.Value);
            }

            if (query.DestinationType.HasValue)
            {
                sql.AppendLine("AND destination_type = @destination_type");
                command.Parameters.AddWithValue("@destination_type", query.DestinationType.Value.ToString());
            }

            if (query.ScheduleState.HasValue)
            {
                sql.AppendLine("AND schedule_state = @schedule_state");
                command.Parameters.AddWithValue("@schedule_state", query.ScheduleState.Value.ToString());
            }

            if (ShareCursor.TryDecode(query.Cursor, out var cursorTimestamp, out var cursorId))
            {
                sql.AppendLine("AND (updated_at, export_id) < (@cursor_timestamp, @cursor_id)");
                command.Parameters.AddWithValue("@cursor_timestamp", cursorTimestamp);
                command.Parameters.AddWithValue("@cursor_id", cursorId);
            }

            sql.AppendLine("ORDER BY updated_at DESC, export_id DESC");
            sql.AppendLine("LIMIT @limit");
            command.CommandText = sql.ToString();
            command.Parameters.AddWithValue("@limit", limit + 1);

            var items = new List<ShareExportDefinition>(limit + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadDefinition(reader));
            }

            var visible = items.Take(limit).ToArray();
            return new ShareExportDefinitionPage
            {
                Items = visible,
                NextCursor = items.Count > limit
                    ? ShareCursor.Encode(visible[^1].UpdatedAt, visible[^1].ExportId)
                    : null
            };
        });

    public Task<ShareExportDefinition?> GetDefinitionAsync(
        string exportId,
        CancellationToken cancellationToken = default)
        => TranslateStoreFailuresAsync(async () =>
        {
            var sql = $"""
                SELECT export_id, resource_id, service_name, layer_id, display_name,
                       destination_type, destination_status, destination_config, format,
                       schedule, schedule_state, created_at, updated_at, last_run_at, next_run_at
                FROM {_definitionsTable}
                WHERE export_id = @export_id
                """;

            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection.Connection);
            command.Parameters.AddWithValue("@export_id", exportId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadDefinition(reader)
                : null;
        });

    public Task<ShareExportDefinition> CreateDefinitionAsync(
        ShareExportDefinition definition,
        CancellationToken cancellationToken = default)
        => TranslateStoreFailuresAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(definition);

            var sql = $"""
                INSERT INTO {_definitionsTable}
                    (export_id, resource_id, service_name, layer_id, display_name,
                     destination_type, destination_status, destination_config, format,
                     schedule, schedule_state, created_at, updated_at, last_run_at, next_run_at)
                VALUES
                    (@export_id, @resource_id, @service_name, @layer_id, @display_name,
                     @destination_type, @destination_status, @destination_config, @format,
                     @schedule, @schedule_state, @created_at, @updated_at, @last_run_at, @next_run_at)
                """;

            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection.Connection);
            AddDefinitionParameters(command, definition);

            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return definition;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ShareExportStoreConflictException(
                    "Share export definition conflicts with an existing record.",
                    ex);
            }
        });

    public Task<ShareExportDefinition?> UpdateDefinitionAsync(
        ShareExportDefinition definition,
        CancellationToken cancellationToken = default)
        => TranslateStoreFailuresAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(definition);

            var sql = $"""
                UPDATE {_definitionsTable}
                SET resource_id = @resource_id,
                    service_name = @service_name,
                    layer_id = @layer_id,
                    display_name = @display_name,
                    destination_type = @destination_type,
                    destination_status = @destination_status,
                    destination_config = @destination_config,
                    format = @format,
                    schedule = @schedule,
                    schedule_state = @schedule_state,
                    updated_at = @updated_at,
                    last_run_at = @last_run_at,
                    next_run_at = @next_run_at
                WHERE export_id = @export_id
                RETURNING export_id, resource_id, service_name, layer_id, display_name,
                          destination_type, destination_status, destination_config, format,
                          schedule, schedule_state, created_at, updated_at, last_run_at, next_run_at
                """;

            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection.Connection);
            AddDefinitionParameters(command, definition);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadDefinition(reader)
                : null;
        });

    public Task<bool> DeleteDefinitionAsync(
        string exportId,
        CancellationToken cancellationToken = default)
        => TranslateStoreFailuresAsync(async () =>
        {
            var sql = $"""
                DELETE FROM {_definitionsTable}
                WHERE export_id = @export_id
                """;

            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection.Connection);
            command.Parameters.AddWithValue("@export_id", exportId);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        });

    public Task<ShareExportRun> AppendRunAsync(
        ShareExportRun run,
        CancellationToken cancellationToken = default)
        => TranslateStoreFailuresAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(run);

            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var insertSql = $"""
                    INSERT INTO {_runsTable}
                        (run_id, export_id, trigger_kind, status, job_run_id, triggered_at,
                         started_at, completed_at, target_summary, result_artifacts, last_error)
                    VALUES
                        (@run_id, @export_id, @trigger_kind, @status, @job_run_id, @triggered_at,
                         @started_at, @completed_at, @target_summary, @result_artifacts, @last_error)
                    """;
                await using (var insert = new NpgsqlCommand(insertSql, connection.Connection, transaction))
                {
                    AddRunParameters(insert, run);
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                var updateSql = $"""
                    UPDATE {_definitionsTable}
                    SET last_run_at = GREATEST(COALESCE(last_run_at, @triggered_at), @triggered_at),
                        updated_at = GREATEST(updated_at, @triggered_at)
                    WHERE export_id = @export_id
                    """;
                await using (var update = new NpgsqlCommand(updateSql, connection.Connection, transaction))
                {
                    update.Parameters.AddWithValue("@export_id", run.ExportId);
                    update.Parameters.AddWithValue("@triggered_at", run.TriggeredAt);
                    await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return run;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new ShareExportStoreConflictException(
                    "Share export run conflicts with an existing record.",
                    ex);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        });

    public Task<ShareExportRunPage> ListRunsAsync(
        string exportId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
        => TranslateStoreFailuresAsync(async () =>
        {
            var effectiveLimit = Math.Clamp(limit, 1, 200);
            var sql = new StringBuilder($"""
                SELECT run_id, export_id, trigger_kind, status, job_run_id, triggered_at,
                       started_at, completed_at, target_summary, result_artifacts, last_error
                FROM {_runsTable}
                WHERE export_id = @export_id
                """);
            sql.AppendLine();

            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand { Connection = connection.Connection };
            command.Parameters.AddWithValue("@export_id", exportId);

            if (ShareCursor.TryDecode(cursor, out var cursorTimestamp, out var cursorId))
            {
                sql.AppendLine("AND (triggered_at, run_id) < (@cursor_timestamp, @cursor_id)");
                command.Parameters.AddWithValue("@cursor_timestamp", cursorTimestamp);
                command.Parameters.AddWithValue("@cursor_id", cursorId);
            }

            sql.AppendLine("ORDER BY triggered_at DESC, run_id DESC");
            sql.AppendLine("LIMIT @limit");
            command.CommandText = sql.ToString();
            command.Parameters.AddWithValue("@limit", effectiveLimit + 1);

            var items = new List<ShareExportRun>(effectiveLimit + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadRun(reader));
            }

            var visible = items.Take(effectiveLimit).ToArray();
            return new ShareExportRunPage
            {
                Items = visible,
                NextCursor = items.Count > effectiveLimit
                    ? ShareCursor.Encode(visible[^1].TriggeredAt, visible[^1].RunId)
                    : null
            };
        });

    public Task<ShareExportRun?> GetRunAsync(
        string exportId,
        string runId,
        CancellationToken cancellationToken = default)
        => TranslateStoreFailuresAsync(async () =>
        {
            var sql = $"""
                SELECT run_id, export_id, trigger_kind, status, job_run_id, triggered_at,
                       started_at, completed_at, target_summary, result_artifacts, last_error
                FROM {_runsTable}
                WHERE export_id = @export_id AND run_id = @run_id
                """;

            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection.Connection);
            command.Parameters.AddWithValue("@export_id", exportId);
            command.Parameters.AddWithValue("@run_id", runId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadRun(reader)
                : null;
        });

    private static void AddDefinitionParameters(NpgsqlCommand command, ShareExportDefinition definition)
    {
        command.Parameters.AddWithValue("@export_id", definition.ExportId);
        command.Parameters.AddWithValue("@resource_id", (object?)definition.ResourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("@service_name", definition.ServiceName);
        command.Parameters.AddWithValue("@layer_id", definition.LayerId);
        command.Parameters.AddWithValue("@display_name", (object?)definition.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("@destination_type", definition.DestinationType.ToString());
        command.Parameters.AddWithValue("@destination_status", definition.DestinationStatus.ToString());
        command.Parameters.Add(new NpgsqlParameter("@destination_config", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(
                new Dictionary<string, string>(definition.DestinationConfig, StringComparer.Ordinal),
                SharePostgresJsonContext.Default.DictionaryStringString)
        });
        command.Parameters.AddWithValue("@format", definition.Format);
        command.Parameters.AddWithValue("@schedule", definition.Schedule);
        command.Parameters.AddWithValue("@schedule_state", definition.ScheduleState.ToString());
        command.Parameters.AddWithValue("@created_at", definition.CreatedAt);
        command.Parameters.AddWithValue("@updated_at", definition.UpdatedAt);
        command.Parameters.AddWithValue("@last_run_at", (object?)definition.LastRunAt ?? DBNull.Value);
        command.Parameters.AddWithValue("@next_run_at", (object?)definition.NextRunAt ?? DBNull.Value);
    }

    private static void AddRunParameters(NpgsqlCommand command, ShareExportRun run)
    {
        command.Parameters.AddWithValue("@run_id", run.RunId);
        command.Parameters.AddWithValue("@export_id", run.ExportId);
        command.Parameters.AddWithValue("@trigger_kind", run.TriggerKind.ToString());
        command.Parameters.AddWithValue("@status", run.Status.ToString());
        command.Parameters.AddWithValue("@job_run_id", (object?)run.JobRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("@triggered_at", run.TriggeredAt);
        command.Parameters.AddWithValue("@started_at", (object?)run.StartedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("@completed_at", (object?)run.CompletedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("@target_summary", (object?)run.TargetSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("@result_artifacts", run.ResultArtifacts.ToArray());
        command.Parameters.AddWithValue("@last_error", (object?)run.LastError ?? DBNull.Value);
    }

    private static ShareExportDefinition ReadDefinition(NpgsqlDataReader reader)
    {
        var configJson = reader.GetString(7);
        var config = JsonSerializer.Deserialize(
                configJson,
                SharePostgresJsonContext.Default.DictionaryStringString)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        return new ShareExportDefinition
        {
            ExportId = reader.GetString(0),
            ResourceId = reader.IsDBNull(1) ? null : reader.GetString(1),
            ServiceName = reader.GetString(2),
            LayerId = reader.GetInt32(3),
            DisplayName = reader.IsDBNull(4) ? null : reader.GetString(4),
            DestinationType = Enum.Parse<ShareExportDestinationType>(reader.GetString(5), ignoreCase: true),
            DestinationStatus = Enum.Parse<ShareExportDestinationStatus>(reader.GetString(6), ignoreCase: true),
            DestinationConfig = config,
            Format = reader.GetString(8),
            Schedule = reader.GetString(9),
            ScheduleState = Enum.Parse<ShareExportScheduleState>(reader.GetString(10), ignoreCase: true),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(11),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(12),
            LastRunAt = reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
            NextRunAt = reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14)
        };
    }

    private static ShareExportRun ReadRun(NpgsqlDataReader reader)
        => new()
        {
            RunId = reader.GetString(0),
            ExportId = reader.GetString(1),
            TriggerKind = Enum.Parse<ShareExportTriggerKind>(reader.GetString(2), ignoreCase: true),
            Status = Enum.Parse<ShareExportRunStatus>(reader.GetString(3), ignoreCase: true),
            JobRunId = reader.IsDBNull(4) ? null : reader.GetString(4),
            TriggeredAt = reader.GetFieldValue<DateTimeOffset>(5),
            StartedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            CompletedAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            TargetSummary = reader.IsDBNull(8) ? null : reader.GetString(8),
            ResultArtifacts = reader.GetFieldValue<string[]>(9),
            LastError = reader.IsDBNull(10) ? null : reader.GetString(10)
        };

    private static async Task<T> TranslateStoreFailuresAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ShareExportStoreConflictException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ServiceUnavailableException ex)
        {
            throw new ShareExportStoreUnavailableException("Share export store is unavailable.", ex);
        }
        catch (NpgsqlException ex)
        {
            throw new ShareExportStoreUnavailableException("Share export store is unavailable.", ex);
        }
        catch (TimeoutException ex)
        {
            throw new ShareExportStoreUnavailableException("Share export store is unavailable.", ex);
        }
    }
}
