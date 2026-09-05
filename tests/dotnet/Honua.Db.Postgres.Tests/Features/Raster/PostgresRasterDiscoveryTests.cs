// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace Honua.Db.Postgres.Tests.Features.Raster;

public sealed class PostgresRasterDiscoveryTests
{
    [IntegrationTest]
    public async Task GetPrimaryRasterInfoAsync_WithoutOptionalRasterExtension_ReturnsNull()
    {
        await using var database = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await database.StartAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.GetConnectionString());
        var provider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
        provider.OpenConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(async call => (DbConnection)await dataSource.OpenConnectionAsync(call.Arg<CancellationToken>()));
        var store = new PostgresRasterStore(provider, NullLogger<PostgresRasterStore>.Instance,
            FixtureBypassDatabaseSchemaGuard.Instance, "honua");

        (await store.GetPrimaryRasterInfoAsync(2110)).Should().BeNull();
    }
}
