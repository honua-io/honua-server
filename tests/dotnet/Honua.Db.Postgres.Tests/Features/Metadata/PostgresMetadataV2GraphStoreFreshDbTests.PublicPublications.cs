// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Metadata;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Metadata;

public sealed partial class PostgresMetadataV2GraphStoreFreshDbTests
{
    [IntegrationTheory]
    [InlineData("pending", "save")]
    [InlineData("ready", "save")]
    [InlineData("revoking", "save")]
    [InlineData("pending", "activate")]
    [InlineData("ready", "activate")]
    [InlineData("revoking", "activate")]
    public async Task Activate_PublicGenerationWithoutRevoker_PreservesCurrentRevision(string state, string operation)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("public_publication_revocation");
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            var store = new PostgresMetadataV2GraphStore(new TestConnectionProvider(fixture.DataSource, schema),
                "Test", FixtureBypassDatabaseSchemaGuard.Instance, schema);
            var graph = new MetadataV2Graph
            {
                Environment = "Test",
                Revision = 1,
                GeneratedAt = DateTimeOffset.UtcNow,
                Resources =
                [
                    new MetadataV2Object
                    {
                        Metadata = new MetadataV2ObjectMetadata { Id = "public-layer", Name = "public-layer" },
                        Type = MetadataV2ObjectType.FeatureDataset
                    }
                ]
            };
            var original = await store.SaveAsync(graph, expectedEtag: null);
            var staged = await store.StageAsync(graph with { GeneratedAt = graph.GeneratedAt.AddSeconds(1) });
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE "{schema}".public_publication_generations (
                    environment text NOT NULL,
                    publication_id text NOT NULL,
                    version text NOT NULL,
                    metadata_revision bigint NOT NULL,
                    state text NOT NULL,
                    PRIMARY KEY (environment, publication_id, version));
                INSERT INTO "{schema}".public_publication_generations
                    (environment, publication_id, version, metadata_revision, state)
                VALUES ('Test', 'public-layer', 'generation-one', @revision, @state);
                """;
            command.Parameters.AddWithValue("revision", original.Revision);
            command.Parameters.AddWithValue("state", state);
            await command.ExecuteNonQueryAsync();

            var save = () => operation == "save"
                ? store.SaveAsync(graph with { GeneratedAt = graph.GeneratedAt.AddSeconds(2) }, original.Etag)
                : store.ActivateRevisionAsync(staged.Revision, original.Etag);

            await save.Should().ThrowAsync<InvalidOperationException>(
                "catalog changes must not leave previously copied public bytes reachable without a revoker");
            (await store.GetCurrentAsync()).Revision.Should().Be(original.Revision);
            command.CommandText = $"SELECT COUNT(*) FROM \"{schema}\".public_publication_generations";
            command.Parameters.Clear();
            (await command.ExecuteScalarAsync()).Should().Be(1L,
                "the durable cleanup obligation must survive a failed catalog change");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }
}
