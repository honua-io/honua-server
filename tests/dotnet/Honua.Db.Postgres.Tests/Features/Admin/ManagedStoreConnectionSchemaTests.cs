// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Reflection;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Db.Postgres.Features.Admin;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Admin;

[Collection("Database")]
public sealed class ManagedStoreConnectionSchemaTests(PostgresFixture fixture)
{
    [Fact]
    public async Task VerifyManagedStoreConnection_WhenPublishingRoleHasAnotherSearchPath_AcceptsTheServerFeaturesTable()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("managed_store_role");
        try
        {
            await using (var setup = await fixture.DataSource.OpenConnectionAsync())
            await using (var command = setup.CreateCommand())
            {
                command.CommandText = $"CREATE TABLE \"{schema}\".features (objectid bigint);";
                await command.ExecuteNonQueryAsync();
            }

            // The server's own connection resolves features in the isolated schema; a second
            // role on the same database does not have that schema on its search path.
            var serverConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                SearchPath = $"{schema},public"
            }.ConnectionString;
            var publishingConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                SearchPath = "pg_catalog"
            }.ConnectionString;
            var service = CreateService(serverConnectionString);

            await using var publishing = new NpgsqlConnection(publishingConnectionString);
            await publishing.OpenAsync();
            await FluentActions.Awaiting(() => VerifyAsync(service, publishing)).Should().NotThrowAsync(
                "the publishing connection reaches the same database and the same features table");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static PostgreSqlLayerPublishingService CreateService(string serverConnectionString)
    {
        var provider = new Mock<IAdoNetDatabaseConnectionProvider>();
        provider.Setup(value => value.OpenConnectionAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken cancellationToken) =>
            {
                var connection = new NpgsqlConnection(serverConnectionString);
                await connection.OpenAsync(cancellationToken);
                return (DbConnection)connection;
            });
        return new PostgreSqlLayerPublishingService(
            Mock.Of<ITableDiscoveryService>(),
            Mock.Of<IMetadataV2GraphStore>(),
            NullLogger<PostgreSqlLayerPublishingService>.Instance,
            featureStoreConnections: provider.Object);
    }

    private static Task VerifyAsync(PostgreSqlLayerPublishingService service, NpgsqlConnection publishing)
    {
        var verify = typeof(PostgreSqlLayerPublishingService).GetMethod(
            "VerifyManagedStoreConnectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)verify.Invoke(service, [publishing, null, CancellationToken.None, null])!;
    }
}
