// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Infrastructure;

/// <summary>
/// Behind RDS Proxy a session <c>SET</c> pins the proxy session for its lifetime. On Lambda the
/// named and bound pools are built unpooled, so closing a connection ends the pinned session
/// while the session settings still apply to every open (honua-server#6760).
/// </summary>
[Collection("Database")]
public sealed class PostgresSessionPinReleaseIntegrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task CreateForBoundSource_ReleaseSessionOnClose_AppliesSettingsAndEndsSessionOnClose()
    {
        var applicationName = "honua-6760-" + Guid.NewGuid().ToString("N")[..12];
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            ApplicationName = applicationName,
        }.ConnectionString;
        var limits = new ConnectionLimits { LockTimeout = TimeSpan.FromSeconds(7) };

        await using var dataSource = PostgresDataSourceFactory.CreateForBoundSource(
            connectionString, limits, releaseSessionOnClose: true);

        int firstPid;
        await using (var connection = await dataSource.OpenConnectionAsync())
        {
            Assert.Equal("7s", await ScalarAsync<string>(connection, "SHOW lock_timeout"));
            firstPid = await ScalarAsync<int>(connection, "SELECT pg_backend_pid()");
        }

        await using (var connection = await dataSource.OpenConnectionAsync())
        {
            Assert.Equal("7s", await ScalarAsync<string>(connection, "SHOW lock_timeout"));
            Assert.NotEqual(firstPid, await ScalarAsync<int>(connection, "SELECT pg_backend_pid()"));
        }

        Assert.Equal(0, await WaitForSessionCountAsync(applicationName, expected: 0));
    }

    private async Task<long> WaitForSessionCountAsync(string applicationName, long expected)
    {
        long count = -1;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await using var admin = new NpgsqlConnection(fixture.ConnectionString);
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE application_name = @name", admin);
            command.Parameters.AddWithValue("name", applicationName);
            count = (long)(await command.ExecuteScalarAsync())!;
            if (count == expected)
            {
                return count;
            }

            await Task.Delay(100);
        }

        return count;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
