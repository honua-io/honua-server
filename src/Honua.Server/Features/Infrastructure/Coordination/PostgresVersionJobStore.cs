// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.ControlPlane;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Infrastructure.Coordination;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Infrastructure.Coordination;

// A job moves from pending to running to a terminal status. A save that would move it back, or
// change a terminal status or job identity, is ignored while holding the row lock, so a late
// write from any node never overwrites a later state or another job's authoritative record.
internal sealed partial class PostgresVersionJobStore(JobStoreDatabase database, ILogger<PostgresVersionJobStore> logger)
    : IVersionJobStore, IExpiredRowSweep
{
    private static readonly TimeSpan _retention = TimeSpan.FromDays(7);

    private readonly string _jobs = database.Table("version_jobs");

    public Task SaveAsync(VersionJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        return database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await using var command = new NpgsqlCommand(
                    $"""
                    INSERT INTO {_jobs} (job_id, stage, record, expires_at)
                    VALUES (@job_id, @stage, @record, now() + @retention)
                    ON CONFLICT (job_id) DO NOTHING
                    """,
                    connection, transaction);
                var parameters = command.Parameters;
                parameters.Add(new NpgsqlParameter("job_id", NpgsqlDbType.Uuid) { Value = job.JobId });
                parameters.Add(new NpgsqlParameter("stage", NpgsqlDbType.Smallint) { Value = Stage(job.Status) });
                parameters.Add(new NpgsqlParameter("record", NpgsqlDbType.Json)
                {
                    Value = JsonSerializer.Serialize(job, VersionJobJsonContext.Default.VersionJob)
                });
                parameters.Add(new NpgsqlParameter("retention", NpgsqlDbType.Interval) { Value = _retention });
                if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 0)
                {
                    await using var read = new NpgsqlCommand(
                        $"SELECT record::text FROM {_jobs} WHERE job_id = @job_id FOR UPDATE",
                        connection, transaction);
                    read.Parameters.AddWithValue("job_id", job.JobId);
                    var record = await read.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
                    var current = Deserialize(job.JobId, record);
                    if (current is not null && HasSameIdentity(current, job) && Stage(current.Status) < Stage(job.Status))
                    {
                        command.CommandText = $"""
                            UPDATE {_jobs} SET stage = @stage, record = @record, expires_at = now() + @retention
                            WHERE job_id = @job_id
                            """;
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }

                await transaction.CommitAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);
    }

    // An unreadable record is reported and treated as an unknown job; the row is kept.
    public async Task<VersionJob?> GetAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var record = await database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"SELECT record::text FROM {_jobs} WHERE job_id = @job_id AND expires_at > now()",
                    connection);
                command.Parameters.Add(new NpgsqlParameter("job_id", NpgsqlDbType.Uuid) { Value = jobId });
                return await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);
        return Deserialize(jobId, record);
    }

    private VersionJob? Deserialize(Guid jobId, string? record)
    {
        if (record is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(record, VersionJobJsonContext.Default.VersionJob);
        }
        catch (JsonException exception)
        {
            Log.UnreadableVersionJob(logger, jobId, exception);
            return null;
        }
    }

    private static bool HasSameIdentity(VersionJob current, VersionJob next)
        => current.JobId == next.JobId && current.Service == next.Service && current.VersionId == next.VersionId
            && current.Kind == next.Kind && current.Policy == next.Policy && current.CreatedAt == next.CreatedAt
            && current.ConflictDetection == next.ConflictDetection;

    public Task<int> DeleteExpiredAsync(int batchSize, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    WITH expired AS (
                        SELECT job_id FROM {_jobs}
                        WHERE expires_at <= now()
                        ORDER BY expires_at LIMIT @batch_size FOR UPDATE SKIP LOCKED)
                    DELETE FROM {_jobs} AS job USING expired WHERE job.job_id = expired.job_id
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer) { Value = batchSize });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private static partial class Log
    {
        [LoggerMessage(7114, LogLevel.Warning, "Version job {JobId} has an unreadable record; treating it as unknown.")]
        public static partial void UnreadableVersionJob(ILogger logger, Guid jobId, Exception exception);
    }

    private static short Stage(VersionJobStatus status) => status switch
    {
        VersionJobStatus.Pending => 0,
        VersionJobStatus.Running => 1,
        _ => 2
    };
}
