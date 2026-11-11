// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;
using NpgsqlTypes;

namespace Honua.ControlPlane;

internal readonly record struct JobStoreRetentionSweepResult(
    long LogStreams,
    long ResultPackages,
    long Jobs,
    long Leases)
{
    public long Total => LogStreams + ResultPackages + Jobs + Leases;
}

// Every delete is bounded, skips rows another node has locked and is safe to repeat, so the sweep
// runs on every serving node without leader election. A job that is not terminal is never deleted.
internal sealed class JobStoreRetentionSweeper(JobStoreDatabase database, IOptions<JobStoreOptions> options)
{
    // A log stream cascades to as many as 2,000 entries and 8,192 event identities.
    internal const int LogStreamBatchSize = 10;

    private const string TerminalStatuses = "'Succeeded', 'Failed', 'Cancelled'";

    private readonly string _deleteExpiredLogStreams = $"""
        WITH expired AS (
            SELECT stream.operation_id FROM {database.Table("execution_job_log_streams")} AS stream
            WHERE stream.expires_at <= now()
              AND NOT EXISTS (
                  SELECT 1 FROM {database.Table(ExecutionJobRow.TableName)} AS job
                  WHERE job.operation_id = stream.operation_id AND job.status NOT IN ({TerminalStatuses}))
            ORDER BY stream.expires_at LIMIT @batch_size FOR UPDATE SKIP LOCKED)
        DELETE FROM {database.Table("execution_job_log_streams")} AS stream USING expired
        WHERE stream.operation_id = expired.operation_id
        """;

    private readonly string _deleteExpiredResultPackages = DeleteExpired(
        database.Table("geoprocessing_result_packages"), "job_id", "expires_at <= now()");

    private readonly string _deleteExpiredJobs = DeleteExpired(
        database.Table(ExecutionJobRow.TableName), "operation_id", $"status IN ({TerminalStatuses}) AND expires_at <= now()");

    private readonly string _deleteExpiredLeases = DeleteExpired(
        database.Table("leases"), "lease_key", "expires_at <= now()");

    // Logs and result packages go before the jobs they belong to, so a sweep that stops part-way
    // never leaves a job's children behind without the job that explains them.
    public async Task<JobStoreRetentionSweepResult> SweepAsync(CancellationToken cancellationToken)
    {
        var batchSize = Math.Max(1, options.Value.RetentionSweepBatchSize);
        var logStreams = await DeleteInBatchesAsync(_deleteExpiredLogStreams, LogStreamBatchSize, cancellationToken)
            .ConfigureAwait(false);
        var resultPackages = await DeleteInBatchesAsync(_deleteExpiredResultPackages, batchSize, cancellationToken)
            .ConfigureAwait(false);
        var jobs = await DeleteInBatchesAsync(_deleteExpiredJobs, batchSize, cancellationToken).ConfigureAwait(false);
        var leases = await DeleteInBatchesAsync(_deleteExpiredLeases, batchSize, cancellationToken).ConfigureAwait(false);
        return new JobStoreRetentionSweepResult(logStreams, resultPackages, jobs, leases);
    }

    private static string DeleteExpired(string table, string keyColumn, string expiredPredicate)
        => $"""
            WITH expired AS (
                SELECT {keyColumn} FROM {table}
                WHERE {expiredPredicate}
                ORDER BY expires_at LIMIT @batch_size FOR UPDATE SKIP LOCKED)
            DELETE FROM {table} AS target USING expired WHERE target.{keyColumn} = expired.{keyColumn}
            """;

    private async Task<long> DeleteInBatchesAsync(string statement, int batchSize, CancellationToken cancellationToken)
    {
        long deleted = 0;
        int deletedInBatch;
        do
        {
            deletedInBatch = await database.ExecuteAsync(
                async (connection, _, token) =>
                {
                    await using var command = new NpgsqlCommand(statement, connection);
                    command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer) { Value = batchSize });
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                },
                StatementRepeat.SafeToRepeat,
                cancellationToken).ConfigureAwait(false);
            deleted += deletedInBatch;
        }
        while (deletedInBatch >= batchSize);

        return deleted;
    }
}
