// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Metadata;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Tests.Features.Metadata;

public sealed partial class PostgresMetadataV2GraphStoreFreshDbTests
{
    [IntegrationTest]
    public async Task ActivateRevisionAsync_StoreProfile_RefusesExcludedRetainedConnectionsBeforeChangingCurrent()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresMetadataV2GraphStoreFreshDbTests));
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresMetadataV2GraphStore(provider, "Test", FixtureBypassDatabaseSchemaGuard.Instance, schema);
            var retained = await store.SaveAsync(new MetadataV2Graph { Environment = "Test" }, null);
            var current = await store.SaveAsync(retained.Graph, retained.Etag);
            await AddRetainedStoreConnectionAsync(schema, retained);

            var postgisOnly = Environment.GetEnvironmentVariable("HONUA_EXPECTED_STORE_PROFILE") == "postgis";
            Func<Task> activate = () => store.ActivateRevisionAsync(retained.Revision, current.Etag);
            if (postgisOnly)
                await activate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*-full*");
            else
                await activate.Should().NotThrowAsync();

            var fresh = new PostgresMetadataV2GraphStore(provider, "Test", FixtureBypassDatabaseSchemaGuard.Instance, schema);
            (await fresh.GetCurrentAsync()).Revision.Should().Be(postgisOnly ? current.Revision : retained.Revision);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task AddRetainedStoreConnectionAsync(string schema, MetadataV2GraphSnapshot retained)
    {
        var graph = retained.Graph with
        {
            Connections = [new MetadataV2Connection
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "retained-store", Name = "retained-store" },
                Type = MetadataV2ConnectionType.Database,
                Provider = "mysql"
            }]
        };
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE \"{schema}\".metadata_v2_snapshots SET document = @document WHERE environment = 'Test' AND revision = @revision";
        command.Parameters.AddWithValue("document", NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(graph, MetadataV2JsonContext.Default.MetadataV2Graph));
        command.Parameters.AddWithValue("revision", retained.Revision);
        await command.ExecuteNonQueryAsync();
    }
}
