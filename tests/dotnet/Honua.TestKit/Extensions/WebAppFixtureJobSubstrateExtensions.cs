// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace Honua.TestKit.Extensions;

public static class WebAppFixtureJobSubstrateExtensions
{
    public const string JobSubstrateCollection = "JobSubstrate";

    private static readonly string[] _jobSubstrateTables =
    [
        "execution_jobs",
        "leases",
        "execution_job_log_streams",
        "geoprocessing_result_packages",
    ];

    // A Test host registers its providers after the feature graph, so the job store, queue and
    // worker are composed only when the host registers infrastructure in production order.
    public static WebAppFixture WithProductionJobSubstrate(this WebAppFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return fixture.ConfigureWebHost(builder => builder.UseSetting("HONUA_REGISTER_TEST_INFRASTRUCTURE", "true"));
    }

    // Hosts in one database share the job tables, so a job left queued by one test would be claimed by the next host's worker.
    public static async Task ClearJobSubstrateAsync(this NpgsqlDataSource dataSource, string schema = "honua")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(schema);
        var tables = string.Join(", ", _jobSubstrateTables.Select(table => $"{quotedSchema}.{table}"));
        await using var command = dataSource.CreateCommand($"TRUNCATE {tables} CASCADE");
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
