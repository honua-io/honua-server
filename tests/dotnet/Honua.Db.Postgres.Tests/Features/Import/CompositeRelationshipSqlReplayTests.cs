// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Db.Postgres.Features.Migration;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

[Collection("Database")]
public sealed class CompositeRelationshipSqlReplayTests(PostgresFixture fixture) : IAsyncLifetime
{
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await fixture.CreateIsolatedDatabaseAsync(nameof(CompositeRelationshipSqlReplayTests));
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        foreach (var file in new[] { "001_CreateHonuaSchema.sql", "003_CreateRelationshipsTable.sql" })
        {
            var sql = await File.ReadAllTextAsync(RepositoryPaths.Resolve("src", "Honua.Server", "Migrations", file));
            await using var migration = new NpgsqlCommand(sql, connection);
            await migration.ExecuteNonQueryAsync();
        }

        await using var seed = new NpgsqlCommand("""
            INSERT INTO honua.layers (layer_id, layer_name, table_name, geometry_type)
            VALUES (10, 'parents', 'parents', 'Point'), (11, 'children', 'children', 'Point'),
                   (12, 'unrelated', 'unrelated', 'Point');
            """, connection);
        await seed.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => fixture.DropDatabaseAsync(new NpgsqlConnectionStringBuilder(_connectionString).Database!);

    [IntegrationTheory]
    [InlineData("matching")]
    [InlineData("related-layer")]
    [InlineData("role")]
    [InlineData("origin-key")]
    [InlineData("destination-key")]
    [InlineData("name")]
    public async Task EnsureRelationshipsAsync_ExistingSqlRow_RequiresMatchingPersistedMapping(string difference)
    {
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var seed = new NpgsqlCommand("""
                INSERT INTO honua.relationships
                    (layer_id, relationship_id, name, related_layer_id, relationship_type,
                     origin_foreign_key, destination_foreign_key)
                VALUES (10, 3, @name, @related, @role, @origin, @destination);
                """, connection);
            seed.Parameters.AddWithValue("name", difference == "name" ? "Different summary" : "Summary");
            seed.Parameters.AddWithValue("related", difference == "related-layer" ? 12 : 11);
            seed.Parameters.AddWithValue("role", difference == "role" ? "esriRelRoleDestination" : "esriRelRoleOrigin");
            seed.Parameters.AddWithValue("origin", difference == "origin-key" ? "Wrong_ID" : "Join_ID");
            seed.Parameters.AddWithValue("destination", difference == "destination-key" ? "Wrong_ID" : "Join_ID");
            await seed.ExecuteNonQueryAsync();
        }

        var before = await ReadOriginRowAsync();
        var graphStore = new GraphStore();
        var writer = new PostgresMigrationCatalogWriter(NullLogger<PostgresMigrationCatalogWriter>.Instance);
        // ArcGIS declares the same relationship id on both resources. Role aliases
        // must match canonical SQL roles, and one result must not overwrite its peer.
        var requests = new[] { Request(10, 11, "origin"), Request(11, 10, "destination") };
        var first = await writer.EnsureRelationshipsAsync(_connectionString, graphStore, requests);
        var replay = await writer.EnsureRelationshipsAsync(_connectionString, graphStore, requests);

        foreach (var outcomes in new[] { first, replay })
        {
            outcomes.Should().HaveCount(2);
            var origin = outcomes.Single(outcome => outcome.TargetRelationshipRef == "rel-10-3");
            origin.Deferred.Should().Be(difference != "matching");
            if (difference != "matching")
            {
                origin.Message.Should().Contain("existing SQL relationship does not match");
            }

            outcomes.Single(outcome => outcome.TargetRelationshipRef == "rel-11-3").Deferred.Should().BeFalse();
        }

        replay.Should().OnlyContain(outcome => outcome.Outcome == MigrationCatalogWriteOutcome.AlreadyExists);
        (await ReadOriginRowAsync()).Should().Be(before, "replay must not overwrite a conflicting relationship");
        await using var readback = new NpgsqlConnection(_connectionString);
        await readback.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM honua.relationships;", readback);
        (await count.ExecuteScalarAsync()).Should().Be(2L, "replay must not duplicate either direction");
        graphStore.Current.Graph.Resources.Should().OnlyContain(resource => resource.Relationships.Single().Composite);
    }

    private async Task<string> ReadOriginRowAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT row_to_json(r)::text FROM honua.relationships r
            WHERE layer_id = 10 AND relationship_id = 3;
            """, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static MigrationRelationshipApplyRequest Request(int origin, int related, string role) => new()
    {
        SourceRelationshipId = "3",
        EsriRelationshipId = 3,
        Name = "Summary",
        OriginLayerId = origin,
        RelatedLayerId = related,
        OriginKeyField = "Join_ID",
        DestinationKeyField = "Join_ID",
        Role = role,
        Cardinality = "1:N",
        Composite = true
    };

    // The graph planner is real; only graph persistence is controlled here. These
    // tests target the independent, migrated PostgreSQL relationship readback.
    private sealed class GraphStore : IMetadataV2GraphStore
    {
        public MetadataV2GraphSnapshot Current { get; private set; } = new(
            new MetadataV2Graph
            {
                Revision = 1,
                Resources = new[] { 10, 11 }.Select(id => new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = $"res-layer-{id}", Name = $"layer-{id}" },
                    SchemaFields = [new MetadataV2Field { Name = "Join_ID", Type = MetadataV2FieldType.Integer }],
                    Editing = new MetadataV2ResourceEditing { CanModify = false }
                }).ToArray()
            }, "initial", DateTimeOffset.UnixEpoch);

        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current);

        public Task<MetadataV2GraphSnapshot> SaveAsync(MetadataV2Graph graph, string? expectedEtag,
            CancellationToken cancellationToken = default)
        {
            expectedEtag.Should().Be(Current.Etag);
            Current = new MetadataV2GraphSnapshot(graph, "saved", DateTimeOffset.UnixEpoch);
            return Task.FromResult(Current);
        }

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MetadataV2GraphSnapshot> ActivateRevisionAsync(long revision, string? expectedCurrentEtag,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
