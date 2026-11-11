// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Honua.ControlPlane;

// Geoprocessing result packages in PostgreSQL (ADR-0081). A package past its retention is not
// returned, whether or not the retention sweep has deleted it yet.
internal sealed partial class PostgresGeoprocessingResultPackageStore(
    JobStoreDatabase database,
    IOptionsMonitor<GeoprocessingExecutorOptions> executorOptions,
    ILogger<PostgresGeoprocessingResultPackageStore> logger) : IGeoprocessingResultPackageStore
{
    private readonly string _packages = database.Table("geoprocessing_result_packages");

    public async Task<AnalysisResultPackage?> GetAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return await database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"SELECT package FROM {_packages} WHERE job_id = @job_id AND expires_at > now()",
                    connection);
                command.Parameters.Add(new NpgsqlParameter("job_id", NpgsqlDbType.Text) { Value = jobId });
                return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is string package
                    ? JsonSerializer.Deserialize(package, ControlPlaneJsonContext.Default.AnalysisResultPackage)
                    : null;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetAsync(
        string jobId,
        AnalysisResultPackage package,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(package);

        var payload = JsonSerializer.Serialize(package, ControlPlaneJsonContext.Default.AnalysisResultPackage);
        await database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    INSERT INTO {_packages} (job_id, package, expires_at)
                    VALUES (@job_id, @package, now() + @retention)
                    ON CONFLICT (job_id) DO UPDATE SET package = EXCLUDED.package, expires_at = EXCLUDED.expires_at
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("job_id", NpgsqlDbType.Text) { Value = jobId });
                command.Parameters.Add(new NpgsqlParameter("package", NpgsqlDbType.Json) { Value = payload });
                command.Parameters.Add(new NpgsqlParameter("retention", NpgsqlDbType.Interval)
                {
                    Value = ttl ?? executorOptions.CurrentValue.ResultRetention
                });
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

        Log.ResultPackageStored(logger, jobId, package.ResultPackageId);
    }

    private static partial class Log
    {
        [LoggerMessage(9040, LogLevel.Debug, "Stored geoprocessing result package for job {JobId}: {ResultPackageId}")]
        public static partial void ResultPackageStored(ILogger logger, string jobId, string resultPackageId);
    }
}
