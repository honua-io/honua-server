// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Alerts;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// The advisory-lock leader must not trust <c>NpgsqlConnection.State</c> (#6127).
/// </summary>
public sealed class PostgresAdvisoryLockLeaderElectionStrategyUnitTests
{
    [UnitTest]
    public void CreateDedicatedLockSource_DisablesMultiplexingAndEnablesKeepAlive()
    {
        var input = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Database = "honua",
            Username = "honua",
            Password = "secret",
            PersistSecurityInfo = true,
            Multiplexing = true,
            KeepAlive = 0,
        };

        using var shared = NpgsqlDataSource.Create(input.ConnectionString);
        using var dedicated = PostgresAdvisoryLockLeaderElectionStrategy.CreateDedicatedLockDataSource(shared);
        var parsed = new NpgsqlConnectionStringBuilder(dedicated.ConnectionString);

        Assert.False(parsed.Multiplexing);
        Assert.True(parsed.KeepAlive >= 30);
        Assert.Equal(30, parsed.TcpKeepAliveTime);
        Assert.Contains("secret", dedicated.ConnectionString, StringComparison.Ordinal);
    }
}

[Protocol(ProtocolNames.Infrastructure)]
[Operation(Operations.ContractTesting)]
[Collection("Database.Alerts")]
public sealed class PostgresAdvisoryLockLeaderElectionStrategyTests(DatabaseFixtureAdapter database)
{
    [IntegrationTest(Timeout = 60000)]
    public async Task TryAcquire_AfterBackendTerminated_DoesNotKeepDeadSession()
    {
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            PersistSecurityInfo = true,
            Multiplexing = true,
        };
        await using var shared = NpgsqlDataSource.Create(connection.ConnectionString);
        await using var strategy = new PostgresAdvisoryLockLeaderElectionStrategy(
            shared,
            NullLogger<PostgresAdvisoryLockLeaderElectionStrategy>.Instance);

        Assert.True(await strategy.TryAcquireAsync());
        var pid = await ReadLockPidAsync(shared);
        Assert.True(pid > 0);

        Assert.True(await strategy.TryAcquireAsync());
        Assert.Equal(pid, await ReadLockPidAsync(shared));

        await using (var admin = await shared.OpenConnectionAsync())
        await using (var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", admin))
        {
            terminate.Parameters.AddWithValue("pid", pid);
            var terminated = (bool?)await terminate.ExecuteScalarAsync() ?? false;
            Assert.True(terminated);
        }

        var reacquired = await strategy.TryAcquireAsync();
        var pidAfter = await ReadLockPidAsync(shared);
        if (reacquired)
        {
            Assert.NotEqual(pid, pidAfter);
            Assert.True(pidAfter > 0);
            Assert.True(strategy.IsLeader);
        }
        else
        {
            Assert.False(strategy.IsLeader);
            Assert.NotEqual(pid, pidAfter);
        }
    }

    [IntegrationTest(Timeout = 60000)]
    public async Task TryAcquire_AfterLeaderConnectionSevered_FollowerTakesTheLock()
    {
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            PersistSecurityInfo = true,
            Multiplexing = true,
        };
        await using var shared = NpgsqlDataSource.Create(connection.ConnectionString);
        await using var leader = new PostgresAdvisoryLockLeaderElectionStrategy(
            shared,
            NullLogger<PostgresAdvisoryLockLeaderElectionStrategy>.Instance);
        await using var follower = new PostgresAdvisoryLockLeaderElectionStrategy(
            shared,
            NullLogger<PostgresAdvisoryLockLeaderElectionStrategy>.Instance);

        Assert.True(await leader.TryAcquireAsync());
        Assert.False(await follower.TryAcquireAsync());
        Assert.True(leader.IsLeader);
        Assert.False(follower.IsLeader);

        var leaderPid = await ReadLockPidAsync(shared);
        await using (var admin = await shared.OpenConnectionAsync())
        await using (var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", admin))
        {
            terminate.Parameters.AddWithValue("pid", leaderPid);
            Assert.True((bool?)await terminate.ExecuteScalarAsync() ?? false);
        }

        Assert.True(await follower.TryAcquireAsync());
        Assert.True(follower.IsLeader);
        var followerPid = await ReadLockPidAsync(shared);
        Assert.NotEqual(leaderPid, followerPid);
        Assert.True(followerPid > 0);

        Assert.False(await leader.TryAcquireAsync());
        Assert.False(leader.IsLeader);
        Assert.Equal(followerPid, await ReadLockPidAsync(shared));
    }

    private static async Task<int> ReadLockPidAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        const string sql = """
            SELECT pid
            FROM pg_locks
            WHERE locktype = 'advisory'
              AND granted
              AND objsubid = 1
              AND classid::bigint = @class_id
              AND objid::bigint = @obj_id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("class_id", NpgsqlDbType.Bigint, (long)PostgresAdvisoryLockLeaderElectionStrategy.LockClassId);
        command.Parameters.AddWithValue("obj_id", NpgsqlDbType.Bigint, (long)PostgresAdvisoryLockLeaderElectionStrategy.LockObjectId);
        var pid = await command.ExecuteScalarAsync();
        return pid is int value ? value : 0;
    }
}
