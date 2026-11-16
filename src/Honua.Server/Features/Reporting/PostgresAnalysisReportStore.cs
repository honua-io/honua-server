// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.ControlPlane;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Core.Features.Reporting;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Domain;
using Honua.Server.Features.Reporting.Models;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Reporting;

internal sealed class PostgresAnalysisReportStore(
    JobStoreDatabase database,
    IOptions<ReportingConfiguration> options) : IAnalysisReportStore
{
    private readonly string _table = database.Table("analysis_reports");
    private readonly int _capacity = Math.Max(1, options.Value.Cache.MaxEntries);
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(Math.Max(1, options.Value.Cache.TtlMinutes));

    public Task<AnalysisReport?> TryGetAsync(string jobId, string contractVersion, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentException.ThrowIfNullOrEmpty(contractVersion);
        return database.ExecuteAsync<AnalysisReport?>(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                SELECT body FROM {_table}
                WHERE job_id = @job AND contract_version = @version AND expires_at > CURRENT_TIMESTAMP
                """, connection);
            command.Parameters.AddWithValue("job", jobId);
            command.Parameters.AddWithValue("version", contractVersion);
            var body = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return body is string json ? TryDeserialize(json) : null;
        }, StatementRepeat.SafeToRepeat, cancellationToken);
    }

    // The table is shared by every task, so during a rolling deploy it can hold a body written by
    // another build. A body this build cannot read is a miss: the service rebuilds and overwrites it.
    private static AnalysisReport? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, ReportingJsonContext.Default.AnalysisReport);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task StoreAsync(AnalysisReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var body = JsonSerializer.Serialize(report, ReportingJsonContext.Default.AnalysisReport);
        return database.ExecuteAsync(async (connection, token) =>
        {
            await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            // Serialize capacity enforcement across all replicas using this schema's report cache.
            await using var mutex = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@table, 0))", connection, transaction);
            mutex.Parameters.AddWithValue("table", _table);
            await mutex.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await using var command = new NpgsqlCommand($"""
                DELETE FROM {_table} WHERE expires_at <= CURRENT_TIMESTAMP;
                INSERT INTO {_table} (job_id, contract_version, body, stored_at, expires_at)
                VALUES (@job, @version, @body, clock_timestamp(), clock_timestamp() + @ttl)
                ON CONFLICT (job_id, contract_version) DO UPDATE
                SET body = EXCLUDED.body, stored_at = EXCLUDED.stored_at, expires_at = EXCLUDED.expires_at;
                DELETE FROM {_table} WHERE (job_id, contract_version) IN (
                    SELECT job_id, contract_version FROM {_table}
                    ORDER BY stored_at DESC, job_id, contract_version OFFSET @capacity
                )
                """, connection, transaction);
            command.Parameters.AddWithValue("job", report.JobId);
            command.Parameters.AddWithValue("version", report.ReportContractVersion);
            command.Parameters.AddWithValue("body", NpgsqlDbType.Json, body);
            command.Parameters.AddWithValue("ttl", _ttl);
            command.Parameters.AddWithValue("capacity", _capacity);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await transaction.CommitSafelyAsync(token).ConfigureAwait(false);
        }, StatementRepeat.SafeToRepeat, cancellationToken);
    }

    public Task RemoveAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        return database.ExecuteAsync(async (connection, token) =>
        {
            await using var command = new NpgsqlCommand($"DELETE FROM {_table} WHERE job_id = @job", connection);
            command.Parameters.AddWithValue("job", jobId);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, StatementRepeat.SafeToRepeat, cancellationToken);
    }
}
