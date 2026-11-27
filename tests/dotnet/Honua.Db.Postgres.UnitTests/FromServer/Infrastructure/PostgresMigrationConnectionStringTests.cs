// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using DbUp.Engine.Transactions;
using FluentAssertions;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure;

[Protocol(TestProtocols.TestQuality)]
public sealed class PostgresMigrationConnectionStringTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void BuildMigrationConnectionString_DisablesPoolingAndPinsPublicSearchPath()
    {
        var built = new NpgsqlConnectionStringBuilder(
            PostgresDatabaseMigrationRunner.BuildMigrationConnectionString(
                "Host=localhost;Database=honua;Username=honua;Pooling=true"));

        built.Pooling.Should().BeFalse();
        built.SearchPath.Should().Be("public");
        built.Options.Should().BeNullOrEmpty("startup options are incompatible with RDS Proxy and PgBouncer");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void BuildLockTimeoutSql_FormatsWholeSecondsAndMilliseconds()
    {
        PostgresDatabaseMigrationRunner.BuildLockTimeoutSql(TimeSpan.FromSeconds(10))
            .Should().Be("SET lock_timeout = '10s'");
        PostgresDatabaseMigrationRunner.BuildLockTimeoutSql(TimeSpan.FromMilliseconds(250))
            .Should().Be("SET lock_timeout = '250ms'");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void LockTimeoutConnectionManager_SetsSessionLockTimeoutBeforeExecutingMigrationCommands()
    {
        var executedSql = new List<string>();
        var inner = Substitute.For<IConnectionManager>();
        inner.When(x => x.ExecuteCommandsWithManagedConnection(Arg.Any<Action<Func<IDbCommand>>>()))
            .Do(call =>
            {
                var action = call.Arg<Action<Func<IDbCommand>>>();
                action(() => CreateRecordingCommand(executedSql));
            });

        var manager = new PostgresDatabaseMigrationRunner.LockTimeoutConnectionManager(inner, TimeSpan.FromSeconds(10));

        manager.ExecuteCommandsWithManagedConnection(factory =>
        {
            using var cmd = factory();
            cmd.CommandText = "CREATE TABLE example (id int)";
            cmd.ExecuteNonQuery();
        });

        executedSql.Should().Equal("SET lock_timeout = '10s'", "CREATE TABLE example (id int)");
    }

    private static IDbCommand CreateRecordingCommand(List<string> executedSql)
    {
        var cmd = Substitute.For<IDbCommand>();
        var commandText = string.Empty;
        cmd.CommandText.Returns(_ => commandText);
        cmd.When(x => x.CommandText = Arg.Any<string>()).Do(c => commandText = c.Arg<string>());
        cmd.ExecuteNonQuery().Returns(_ =>
        {
            executedSql.Add(commandText);
            return 0;
        });
        return cmd;
    }
}
