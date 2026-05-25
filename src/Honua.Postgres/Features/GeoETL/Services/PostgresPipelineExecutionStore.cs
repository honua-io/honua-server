// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Postgres.Features.GeoETL.Services;

/// <summary>
/// PostgreSQL-backed <see cref="IPipelineExecutionStore"/>. Persists execution records to
/// <c>honua.pipeline_executions</c> (#361 Child Ticket A — durable persistence). The
/// status is stored as its enum name and correlated to the substrate job by
/// <c>execution_job_id</c>. <see cref="UpdateAsync"/> upserts so a job that resumes after
/// a worker restart converges on the latest state.
/// </summary>
internal sealed class PostgresPipelineExecutionStore : IPipelineExecutionStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _table;

    public PostgresPipelineExecutionStore(
        IDatabaseConnectionProvider connectionProvider,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
        _table = SchemaSearchPath.QualifyTable("pipeline_executions", schemaName);
    }

    public async Task CreateAsync(PipelineExecution execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentException.ThrowIfNullOrWhiteSpace(execution.Id);

        var sql = $"""
            INSERT INTO {_table} (
                id, pipeline_id, pipeline_version, execution_job_id, status, is_dry_run,
                features_read, features_written, features_quarantined, batch_id, error_message,
                created_at, completed_at
            ) VALUES (
                @id, @pipeline_id, @pipeline_version, @execution_job_id, @status, @is_dry_run,
                @features_read, @features_written, @features_quarantined, @batch_id, @error_message,
                @created_at, @completed_at
            )
            """;

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;
        await using var command = new NpgsqlCommand(sql, connection);
        BindExecution(command, execution);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException($"Execution '{execution.Id}' already exists.", ex);
        }
    }

    public async Task UpdateAsync(PipelineExecution execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentException.ThrowIfNullOrWhiteSpace(execution.Id);

        var sql = $"""
            INSERT INTO {_table} (
                id, pipeline_id, pipeline_version, execution_job_id, status, is_dry_run,
                features_read, features_written, features_quarantined, batch_id, error_message,
                created_at, completed_at
            ) VALUES (
                @id, @pipeline_id, @pipeline_version, @execution_job_id, @status, @is_dry_run,
                @features_read, @features_written, @features_quarantined, @batch_id, @error_message,
                @created_at, @completed_at
            )
            ON CONFLICT (id) DO UPDATE SET
                pipeline_id          = EXCLUDED.pipeline_id,
                pipeline_version     = EXCLUDED.pipeline_version,
                execution_job_id     = EXCLUDED.execution_job_id,
                status               = EXCLUDED.status,
                is_dry_run           = EXCLUDED.is_dry_run,
                features_read        = EXCLUDED.features_read,
                features_written     = EXCLUDED.features_written,
                features_quarantined = EXCLUDED.features_quarantined,
                batch_id             = EXCLUDED.batch_id,
                error_message        = EXCLUDED.error_message,
                completed_at         = EXCLUDED.completed_at
            """;

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;
        await using var command = new NpgsqlCommand(sql, connection);
        BindExecution(command, execution);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PipelineExecution?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var sql = $"{SelectColumns()} FROM {_table} WHERE id = @id";

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadExecution(reader);
    }

    public async Task<IReadOnlyList<PipelineExecution>> ListForPipelineAsync(
        string pipelineId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);

        var sql = $"""
            {SelectColumns()}
            FROM {_table}
            WHERE pipeline_id = @pipeline_id
            ORDER BY created_at DESC, id DESC
            """;

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@pipeline_id", pipelineId);

        var results = new List<PipelineExecution>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadExecution(reader));
        }

        return results;
    }

    private static string SelectColumns() =>
        """
        SELECT id, pipeline_id, pipeline_version, execution_job_id, status, is_dry_run,
               features_read, features_written, features_quarantined, batch_id, error_message,
               created_at, completed_at
        """;

    private static void BindExecution(NpgsqlCommand command, PipelineExecution execution)
    {
        command.Parameters.AddWithValue("@id", execution.Id);
        command.Parameters.AddWithValue("@pipeline_id", execution.PipelineId);
        command.Parameters.AddWithValue("@pipeline_version", execution.PipelineVersion);
        command.Parameters.AddWithValue("@execution_job_id", execution.ExecutionJobId);
        command.Parameters.AddWithValue("@status", execution.Status.ToString());
        command.Parameters.AddWithValue("@is_dry_run", execution.IsDryRun);
        command.Parameters.AddWithValue("@features_read", execution.FeaturesRead);
        command.Parameters.AddWithValue("@features_written", execution.FeaturesWritten);
        command.Parameters.AddWithValue("@features_quarantined", execution.FeaturesQuarantined);
        command.Parameters.AddWithValue("@batch_id", (object?)execution.BatchId ?? DBNull.Value);
        command.Parameters.AddWithValue("@error_message", (object?)execution.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("@created_at", execution.CreatedAt);
        command.Parameters.AddWithValue("@completed_at", (object?)execution.CompletedAt ?? DBNull.Value);
    }

    private static PipelineExecution ReadExecution(NpgsqlDataReader reader)
        => new()
        {
            Id = reader.GetString(0),
            PipelineId = reader.GetString(1),
            PipelineVersion = reader.GetInt32(2),
            ExecutionJobId = reader.GetString(3),
            Status = Enum.Parse<PipelineExecutionStatus>(reader.GetString(4), ignoreCase: true),
            IsDryRun = reader.GetBoolean(5),
            FeaturesRead = reader.GetInt64(6),
            FeaturesWritten = reader.GetInt64(7),
            FeaturesQuarantined = reader.GetInt64(8),
            BatchId = reader.IsDBNull(9) ? null : reader.GetString(9),
            ErrorMessage = reader.IsDBNull(10) ? null : reader.GetString(10),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(11),
            CompletedAt = reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12)
        };
}
