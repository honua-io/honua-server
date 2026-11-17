// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Db.Postgres.Features.Metadata;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Metadata;

// A layer publish holds RowExclusiveLock on the feature table and the per-layer publish lock while the
// graph activation runs on another connection. Activation must skip that layer instead of
// waiting for a ShareLock the publisher never releases; once the publisher is gone the layer is indexed.
public sealed partial class PostgresMetadataV2GraphStoreFreshDbTests
{
    [IntegrationTest]
    public async Task SaveAsync_LayerHeldByPublisher_SkipsItsIndexWithoutWaiting()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("global_id_publish");
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE \"{schema}\".features (layer_id integer NOT NULL, attributes jsonb NOT NULL)";
            await command.ExecuteNonQueryAsync();
            var store = new PostgresMetadataV2GraphStore(new TestConnectionProvider(fixture.DataSource, schema),
                "Test", FixtureBypassDatabaseSchemaGuard.Instance, schema);
            await using var publisher = await fixture.DataSource.OpenConnectionAsync();
            await using var publishing = await publisher.BeginTransactionAsync();
            await ExecuteAsync(publisher, publishing, $$"""
                INSERT INTO "{{schema}}".features VALUES (7, '{"globalid":"6f1c2d3e-4b5a-4c6d-8e7f-0a1b2c3d4e5f"}');
                SELECT pg_advisory_xact_lock(hashtextextended(
                    'global-id-publish:' || to_regclass('"{{schema}}".features')::oid::text || ':7', 0));
                """);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var activate = async () => await store.SaveAsync(GlobalIdGraph(schema), expectedEtag: null, timeout.Token);

            await activate.Should().NotThrowAsync();
            (await CountGlobalIdIndexesAsync(command, schema)).Should().Be(0);
            await publishing.CommitAsync();
            command.CommandText = $"SELECT \"{schema}\".sync_metadata_global_id_indexes()";
            await command.ExecuteNonQueryAsync();
            (await CountGlobalIdIndexesAsync(command, schema)).Should().Be(1);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task SaveAsync_NonUuidGlobalIdValue_RefusesWithIntegrityError()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("global_id_non_uuid");
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $$"""
                CREATE TABLE "{{schema}}".features (layer_id integer NOT NULL, attributes jsonb NOT NULL);
                INSERT INTO "{{schema}}".features VALUES (7, '{"globalid":"not-a-uuid"}');
                """;
            await command.ExecuteNonQueryAsync();
            var store = new PostgresMetadataV2GraphStore(new TestConnectionProvider(fixture.DataSource, schema),
                "Test", FixtureBypassDatabaseSchemaGuard.Instance, schema);

            var activate = async () => await store.SaveAsync(GlobalIdGraph(schema), expectedEtag: null);

            (await activate.Should().ThrowAsync<MetadataV2GlobalIdIntegrityException>()).WithInnerException<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.InvalidTextRepresentation);
            (await CountGlobalIdIndexesAsync(command, schema)).Should().Be(0);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountGlobalIdIndexesAsync(NpgsqlCommand command, string schema)
    {
        command.CommandText = $"SELECT COUNT(*)::int FROM pg_indexes WHERE schemaname = '{schema}' AND indexname LIKE 'idx_features_gid_%'";
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
