// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Db.Postgres.Features.Metadata;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Metadata;

public sealed partial class PostgresMetadataV2GraphStoreFreshDbTests
{
    [IntegrationTheory]
    [InlineData("native", null)]
    [InlineData("source", null)]
    [InlineData("source", "PrimaryKeyColumn")]
    [InlineData("source", "GeometryColumn")]
    [InlineData("source", "AttributesColumn")]
    [InlineData("source", "LayerDiscriminatorColumn")]
    public async Task SaveAsync_GlobalIdLayer_EnforcesNormalizedIdentityPerLayer(string bindingKind, string? ignoredOption)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("global_id_integrity");
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE \"{schema}\".features (layer_id integer NOT NULL, attributes jsonb NOT NULL)";
            await command.ExecuteNonQueryAsync();
            var store = new PostgresMetadataV2GraphStore(new TestConnectionProvider(fixture.DataSource, schema),
                "Test", FixtureBypassDatabaseSchemaGuard.Instance, schema);
            var graph = CreateGlobalIdBinding(schema, bindingKind, ignoredOption);
            var mapping = FeatureStorageMapping.FromMetadata(graph.Resources[0], graph.StorageBindings[0]);
            mapping.SupportsManagedWrites.Should().BeTrue();
            mapping.PrimaryKeyColumn.Should().Be("objectid");
            mapping.GeometryColumn.Should().BeNull();
            await store.SaveAsync(graph, expectedEtag: null);

            await AssertGlobalIdRowsConstrainedAsync(schema, command);
            await AssertConcurrentGlobalIdInsertRejectedAsync(schema, connection, command);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTheory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData(".\t.\r\n")]
    public async Task SaveAsync_ExistingDuplicateGlobalIds_LeavesCurrentRevisionUnchanged(string locatorSuffix)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("global_id_activation");
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE \"{schema}\".features (layer_id integer NOT NULL, attributes jsonb NOT NULL)";
            await command.ExecuteNonQueryAsync();
            var store = new PostgresMetadataV2GraphStore(new TestConnectionProvider(fixture.DataSource, schema),
                "Test", FixtureBypassDatabaseSchemaGuard.Instance, schema);
            var declared = GlobalIdGraph(schema);
            declared = declared with
            {
                StorageBindings = [declared.StorageBindings[0] with
            {
                Locator = $"{schema}.features{locatorSuffix}"
            }]
            };
            var mapping = FeatureStorageMapping.FromMetadata(declared.Resources[0], declared.StorageBindings[0]);
            mapping.SchemaName.Should().Be(schema);
            mapping.TableName.Should().Be("features");
            var undeclared = declared with { Resources = [declared.Resources[0] with { Editing = null }] };
            var original = await store.SaveAsync(undeclared, expectedEtag: null);
            await InsertDuplicateGlobalIdsAsync(schema, command);

            var activate = async () => await store.SaveAsync(declared, original.Etag);

            (await activate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
            (await store.GetCurrentAsync())!.Etag.Should().Be(original.Etag);
            command.CommandText = $"SELECT COUNT(*)::int FROM \"{schema}\".features";
            (await command.ExecuteScalarAsync()).Should().Be(2);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task SaveAsync_GlobalIdDeclaration_RemainsEnforcedAcrossEnvironmentChanges()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("global_id_environments");
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE \"{schema}\".features (layer_id integer NOT NULL, attributes jsonb NOT NULL)";
            await command.ExecuteNonQueryAsync();
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var firstStore = new PostgresMetadataV2GraphStore(provider, "Test", FixtureBypassDatabaseSchemaGuard.Instance, schema);
            var secondStore = new PostgresMetadataV2GraphStore(provider, "Other", FixtureBypassDatabaseSchemaGuard.Instance, schema);
            var declared = GlobalIdGraph(schema);
            var first = await firstStore.SaveAsync(declared, expectedEtag: null);
            var second = await secondStore.SaveAsync(declared with { Environment = "Other" }, expectedEtag: null);
            var undeclared = declared with { Resources = [declared.Resources[0] with { Editing = null }] };
            await firstStore.SaveAsync(undeclared, first.Etag);
            command.CommandText = $$"""
                INSERT INTO "{{schema}}".features VALUES
                    (7, '{"globalid":"6f1c2d3e-4b5a-4c6d-8e7f-0a1b2c3d4e5f"}')
                """;
            await command.ExecuteNonQueryAsync();

            var duplicate = async () => await command.ExecuteNonQueryAsync();
            (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);

            await secondStore.SaveAsync(undeclared with { Environment = "Other" }, second.Etag);
            (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task IndexSync_ExistingGlobalIdIndex_DoesNotWaitForFeatureWriters()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("global_id_resync");
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE \"{schema}\".features (layer_id integer NOT NULL, attributes jsonb NOT NULL)";
            await command.ExecuteNonQueryAsync();
            var store = new PostgresMetadataV2GraphStore(new TestConnectionProvider(fixture.DataSource, schema),
                "Test", FixtureBypassDatabaseSchemaGuard.Instance, schema);
            await store.SaveAsync(GlobalIdGraph(schema), expectedEtag: null);
            command.CommandText = $"SELECT COUNT(*)::int FROM pg_indexes WHERE schemaname = '{schema}' AND indexname LIKE 'idx_features_gid_%'";
            (await command.ExecuteScalarAsync()).Should().Be(1);
            await using var writer = await fixture.DataSource.OpenConnectionAsync();
            await using var pendingWrite = await writer.BeginTransactionAsync();
            await using (var insert = new NpgsqlCommand($"INSERT INTO \"{schema}\".features VALUES (7, '{{}}')", writer, pendingWrite))
            {
                await insert.ExecuteNonQueryAsync();
            }

            command.CommandText = $"SET lock_timeout = '2s'; SELECT \"{schema}\".sync_metadata_global_id_indexes()";
            var resync = async () => await command.ExecuteNonQueryAsync();

            await resync.Should().NotThrowAsync();
            await pendingWrite.RollbackAsync();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task SaveAsync_GlobalIdLayer_RejectsDuplicatesHiddenByRowSecurity()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("global_id_rls");
        var role = "global_id_writer_" + Guid.NewGuid().ToString("N");
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            await CreateRowSecuredGlobalIdTableAsync(schema, role);
            var store = new PostgresMetadataV2GraphStore(new TestConnectionProvider(fixture.DataSource, schema),
                "Test", FixtureBypassDatabaseSchemaGuard.Instance, schema);
            await store.SaveAsync(GlobalIdGraph(schema), expectedEtag: null);
            await fixture.ExecuteAsync($$"""
                INSERT INTO "{{schema}}".features VALUES
                    (7, '{"globalid":"6f1c2d3e-4b5a-4c6d-8e7f-0a1b2c3d4e5f"}')
                """);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SET LOCAL ROLE {role}";
            await command.ExecuteNonQueryAsync();
            command.CommandText = $"SELECT COUNT(*)::int FROM \"{schema}\".features";
            (await command.ExecuteScalarAsync()).Should().Be(0);
            command.CommandText = $$"""
                INSERT INTO "{{schema}}".features VALUES
                    (7, '{"globalid":"{6F1c2D3e-4B5a-4C6d-8E7f-0A1b2C3d4E5f}"}')
                """;

            var duplicate = async () => await command.ExecuteNonQueryAsync();

            (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
            await fixture.ExecuteAsync($"DROP ROLE IF EXISTS {role}");
        }
    }

    [IntegrationTest]
    public async Task Migration_ExistingDeclaredGlobalIds_RefusesDuplicatesWithoutChangingRows()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("global_id_backfill");
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            await fixture.ExecuteAsync($$"""
                CREATE TABLE "{{schema}}".features (layer_id integer NOT NULL, attributes jsonb NOT NULL);
                ALTER TABLE "{{schema}}".metadata_v2_current DISABLE TRIGGER metadata_v2_current_global_ids;
                """);
            var store = new PostgresMetadataV2GraphStore(new TestConnectionProvider(fixture.DataSource, schema),
                "Test", FixtureBypassDatabaseSchemaGuard.Instance, schema);
            var original = await store.SaveAsync(GlobalIdGraph(schema), expectedEtag: null);
            await fixture.ExecuteAsync($$"""
                INSERT INTO "{{schema}}".features VALUES
                    (7, '{"globalid":"6f1c2d3e-4b5a-4c6d-8e7f-0a1b2c3d4e5f"}'),
                    (7, '{"globalid":"{6F1c2D3e-4B5a-4C6d-8E7f-0A1b2C3d4E5f}"}');
                ALTER TABLE "{{schema}}".metadata_v2_current ENABLE TRIGGER metadata_v2_current_global_ids;
                """);

            var migrate = async () => await CoreMigrationTestFixture.ApplyGlobalIdIntegrityAsync(fixture, schema);

            (await migrate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
            (await store.GetCurrentAsync())!.Etag.Should().Be(original.Etag);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*)::int FROM \"{schema}\".features";
            (await command.ExecuteScalarAsync()).Should().Be(2);

            await AssertExplicitRepairEnablesMigrationAsync(schema, command);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task SaveAsync_GlobalIdLayer_WithSeparateFeatureSchema_EnforcesIdentity(int optionCase)
    {
        var metadataSchema = await fixture.CreateIsolatedSchemaAsync("global_id_metadata");
        var featureSchema = await fixture.CreateIsolatedSchemaAsync("global_id_features");
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, metadataSchema);
            await fixture.ExecuteAsync($$"""
                CREATE TABLE "{{featureSchema}}".features (layer_id integer NOT NULL, attributes jsonb NOT NULL);
                INSERT INTO "{{featureSchema}}".features VALUES
                    (7, '{"globalid":"6f1c2d3e-4b5a-4c6d-8e7f-0a1b2c3d4e5f"}');
                """);
            // The metadata store qualifies its own tables; the managed writer uses a separate search path.
            var store = new PostgresMetadataV2GraphStore(new TestConnectionProvider(fixture.DataSource, featureSchema),
                "Test", FixtureBypassDatabaseSchemaGuard.Instance, metadataSchema);
            var graph = CreateSeparateSchemaGlobalIdGraph(featureSchema, optionCase);
            var mapping = FeatureStorageMapping.FromMetadata(graph.Resources[0], graph.StorageBindings[0]);
            mapping.SchemaName?.Trim().Should().Be(optionCase is 3 or 4 or 6 ? string.Empty : featureSchema);
            mapping.TableName.Trim().Should().Be("features");
            await store.SaveAsync(graph, expectedEtag: null);

            var duplicate = async () => await fixture.ExecuteAsync($$"""
                INSERT INTO "{{featureSchema}}".features VALUES
                    (7, '{"globalid":"{6F1C2D3E-4B5A-4C6D-8E7F-0A1B2C3D4E5F}"}');
                """);
            (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        }
        finally
        {
            await fixture.DropSchemaAsync(metadataSchema);
            await fixture.DropSchemaAsync(featureSchema);
        }
    }

    [IntegrationTheory]
    [InlineData("local", null)]
    [InlineData("local", "false")]
    [InlineData("external", null)]
    [InlineData("local", "\ttrue\r\n")]
    [InlineData("local", "\u00a0true\u2003")]
    public async Task SaveAsync_SourceBackedGlobalIds_DoesNotConstrainUnrelatedManagedRows(string connectionKind, string? sourceBackedText)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("global_id_source");
        var sourceSchema = await fixture.CreateIsolatedSchemaAsync("global_id_external");
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            await fixture.ExecuteAsync($$"""
                CREATE TABLE "{{schema}}".features (layer_id integer NOT NULL, attributes jsonb NOT NULL);
                CREATE TABLE "{{sourceSchema}}".features (layer_id integer NOT NULL, attributes jsonb NOT NULL);
                INSERT INTO "{{schema}}".features VALUES
                    (7, '{"globalid":"6f1c2d3e-4b5a-4c6d-8e7f-0a1b2c3d4e5f"}'),
                    (7, '{"globalid":"{6F1C2D3E-4B5A-4C6D-8E7F-0A1B2C3D4E5F}"}');
                """);
            await fixture.ExecuteAsync($"INSERT INTO \"{sourceSchema}\".features SELECT * FROM \"{schema}\".features");
            var graph = CreateExternalGlobalIdGraph((schema, sourceSchema), connectionKind, sourceBackedText);
            FeatureStorageMapping.FromMetadata(graph.Resources[0], graph.StorageBindings[0]).IsSourceBacked.Should().Be(sourceBackedText != "false");
            var store = new PostgresMetadataV2GraphStore(new TestConnectionProvider(fixture.DataSource, schema),
                "Test", FixtureBypassDatabaseSchemaGuard.Instance, schema);

            await store.SaveAsync(graph, expectedEtag: null);

            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*)::int FROM pg_indexes WHERE schemaname IN ('{schema}', '{sourceSchema}') AND indexname LIKE 'idx_features_gid_%'";
            (await command.ExecuteScalarAsync()).Should().Be(0);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
            await fixture.DropSchemaAsync(sourceSchema);
        }
    }

}
