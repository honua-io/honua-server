// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// The migration lock is a session advisory lock, and migration scripts may SET session state.
/// Behind RDS Proxy either pins the proxy session, so migration connections must not return to a
/// pool that a frozen Lambda never prunes (honua-server#6760).
/// </summary>
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
    }
}
