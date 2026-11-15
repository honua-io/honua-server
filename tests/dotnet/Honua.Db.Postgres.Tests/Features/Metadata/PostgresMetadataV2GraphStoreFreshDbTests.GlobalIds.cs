// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Db.Postgres.Features.Metadata;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Metadata;

public sealed partial class PostgresMetadataV2GraphStoreFreshDbTests
{
    [IntegrationTheory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "PrimaryKeyColumn")]
    [InlineData(true, "GeometryColumn")]
    [InlineData(true, "AttributesColumn")]
    [InlineData(true, "LayerDiscriminatorColumn")]
    public async Task SaveAsync_GlobalIdLayer_EnforcesNormalizedIdentityPerLayer(bool sourceBacked, string? ignoredOption)
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
            var graph = GlobalIdGraph(schema);
            if (sourceBacked)
            {
                graph = graph with
                {
                    StorageBindings = [graph.StorageBindings[0] with
                    {
                        Options = new Dictionary<string, JsonElement>
                        {
                            ["sourceBacked"] = JsonSerializer.SerializeToElement(true),
                            ["schemaName"] = JsonSerializer.SerializeToElement(schema),
                            ["tableName"] = JsonSerializer.SerializeToElement("features"),
                            ["attributesColumn"] = JsonSerializer.SerializeToElement("attributes"),
                            ["layerDiscriminatorColumn"] = JsonSerializer.SerializeToElement("layer_id")
                        }
                    }]
                };
            }
            if (ignoredOption is not null)
            {
                graph = graph with { StorageBindings = [graph.StorageBindings[0] with
                {
                    Options = new Dictionary<string, JsonElement>(graph.StorageBindings[0].Options)
                    { [ignoredOption] = JsonSerializer.SerializeToElement("other_column") }
                }] };
            }
            var mapping = FeatureStorageMapping.FromMetadata(graph.Resources[0], graph.StorageBindings[0]);
            mapping.SupportsManagedWrites.Should().BeTrue();
            mapping.PrimaryKeyColumn.Should().Be("objectid");
            mapping.GeometryColumn.Should().BeNull();
            await store.SaveAsync(graph, expectedEtag: null);

            command.CommandText = $$"""
                INSERT INTO "{{schema}}".features VALUES
                    (7, '{"globalid":"6f1c2d3e-4b5a-4c6d-8e7f-0a1b2c3d4e5f"}'),
                    (8, '{"globalid":"6f1c2d3e-4b5a-4c6d-8e7f-0a1b2c3d4e5f"}'),
                    (7, '{}'), (7, '{}'), (7, '{"globalid":""}')
                """;
            await command.ExecuteNonQueryAsync();
            command.CommandText = $$"""
                INSERT INTO "{{schema}}".features VALUES
                    (7, '{"globalid":"{6F1c2D3e-4B5a-4C6d-8E7f-0A1b2C3d4E5f}"}')
                """;
            var duplicate = async () => await command.ExecuteNonQueryAsync();
            (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);

            command.CommandText = $$"""
                UPDATE "{{schema}}".features
                SET attributes = '{"globalid":"{6F1c2D3e-4B5a-4C6d-8E7f-0A1b2C3d4E5f}"}'
                WHERE layer_id = 7 AND attributes = '{}'::jsonb
                """;
            (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);

            await using var firstTransaction = await connection.BeginTransactionAsync();
            command.Transaction = firstTransaction;
            command.CommandText = $$"""
                INSERT INTO "{{schema}}".features VALUES
                    (7, '{"globalid":"f65f3d6c-7adc-4c28-a888-b274345b1e14"}')
                """;
            await command.ExecuteNonQueryAsync();
            await using var competingConnection = await fixture.DataSource.OpenConnectionAsync();
            await using var competingCommand = competingConnection.CreateCommand();
            competingCommand.CommandText = $$"""
                INSERT INTO "{{schema}}".features VALUES
                    (7, '{"globalid":"{F65F3D6C-7ADC-4C28-A888-B274345B1E14}"}')
                """;
            var competingWrite = competingCommand.ExecuteNonQueryAsync();
            await firstTransaction.CommitAsync();
            var observeCompetingWrite = async () => await competingWrite;
            (await observeCompetingWrite.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
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
            declared = declared with { StorageBindings = [declared.StorageBindings[0] with
            {
                Locator = $"{schema}.features{locatorSuffix}"
            }] };
            var mapping = FeatureStorageMapping.FromMetadata(declared.Resources[0], declared.StorageBindings[0]);
            mapping.SchemaName.Should().Be(schema);
            mapping.TableName.Should().Be("features");
            var undeclared = declared with { Resources = [declared.Resources[0] with { Editing = null }] };
            var original = await store.SaveAsync(undeclared, expectedEtag: null);
            command.CommandText = $$"""
                INSERT INTO "{{schema}}".features VALUES
                    (7, '{"globalid":"6f1c2d3e-4b5a-4c6d-8e7f-0a1b2c3d4e5f"}'),
                    (7, '{"globalid":"{6F1c2D3e-4B5a-4C6d-8E7f-0A1b2C3d4E5f}"}')
                """;
            await command.ExecuteNonQueryAsync();

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
    public async Task SaveAsync_GlobalIdLayer_RejectsDuplicatesHiddenByRowSecurity()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("global_id_rls");
        var role = "global_id_writer_" + Guid.NewGuid().ToString("N");
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            await fixture.ExecuteAsync($$"""
                CREATE TABLE "{{schema}}".features (layer_id integer NOT NULL, attributes jsonb NOT NULL);
                CREATE ROLE {{role}};
                GRANT USAGE ON SCHEMA "{{schema}}" TO {{role}};
                GRANT SELECT, INSERT ON "{{schema}}".features TO {{role}};
                ALTER TABLE "{{schema}}".features ENABLE ROW LEVEL SECURITY;
                CREATE POLICY visible_rows ON "{{schema}}".features FOR SELECT USING (false);
                CREATE POLICY writable_rows ON "{{schema}}".features FOR INSERT WITH CHECK (true);
                """);
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

            // Only an explicit data repair permits migration; the migration never discards a row.
            command.CommandText = $$"""
                DELETE FROM "{{schema}}".features WHERE attributes->>'globalid' LIKE '{%'
                """;
            await command.ExecuteNonQueryAsync();
            await CoreMigrationTestFixture.ApplyGlobalIdIntegrityAsync(fixture, schema);
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
            var graph = GlobalIdGraph(featureSchema);
            if (optionCase != 0)
            {
                var options = new Dictionary<string, JsonElement>
                {
                    [optionCase == 2 ? "SchemaName" : "schemaName"] = JsonSerializer.SerializeToElement(
                        optionCase switch { 1 => featureSchema, 3 => string.Empty, 4 => "\t", 5 => $"\t{featureSchema}\r\n", 6 => "\u00a0\u2003", _ => "unrelated" }),
                    [optionCase == 2 ? "TableName" : "tableName"] = JsonSerializer.SerializeToElement(optionCase == 5 ? "\tfeatures\n" : "features")
                };
                if (optionCase == 3)
                {
                    options["schema"] = JsonSerializer.SerializeToElement("unrelated");
                }
                graph = graph with
                {
                    StorageBindings = [graph.StorageBindings[0] with
                    {
                        Locator = optionCase == 1 ? "unrelated.features" : $"{featureSchema}.features",
                        Options = options
                    }]
                };
            }
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
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "\ttrue\r\n")]
    [InlineData(false, "\u00a0true\u2003")]
    public async Task SaveAsync_SourceBackedGlobalIds_DoesNotConstrainUnrelatedManagedRows(bool externalConnection, string? sourceBackedText)
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
            var graph = GlobalIdGraph(externalConnection ? schema : sourceSchema);
            graph = graph with
            {
                StorageBindings = [graph.StorageBindings[0] with
                {
                    ConnectionId = externalConnection ? "conn" : null,
                    Options = new Dictionary<string, JsonElement>
                    {
                        ["sourceBacked"] = sourceBackedText is null
                            ? JsonSerializer.SerializeToElement(true)
                            : JsonSerializer.SerializeToElement(sourceBackedText),
                        ["schemaName"] = JsonSerializer.SerializeToElement(externalConnection ? schema : sourceSchema),
                        ["tableName"] = JsonSerializer.SerializeToElement("features"),
                        ["attributesColumn"] = JsonSerializer.SerializeToElement("attributes"),
                        ["layerDiscriminatorColumn"] = JsonSerializer.SerializeToElement("layer_id")
                    }
                }]
            };
            FeatureStorageMapping.FromMetadata(graph.Resources[0], graph.StorageBindings[0]).IsSourceBacked.Should().BeTrue();
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

    private static MetadataV2Graph GlobalIdGraph(string schema) => new()
    {
        Environment = "Test",
        GeneratedAt = DateTimeOffset.UtcNow,
        Connections = [new MetadataV2Connection { Metadata = new() { Id = "conn", Name = "managed" } }],
        StorageBindings = [new MetadataV2StorageBinding
        {
            Metadata = new() { Id = "binding", Name = "features" },
            ResourceId = "resource", StorageLayerId = 7,
            Locator = $"{schema}.features"
        }],
        Resources = [new MetadataV2Object
        {
            Metadata = new() { Id = "resource", Name = "features" },
            StorageBindingIds = ["binding"],
            Editing = new() { GlobalIdField = "globalid" },
            SchemaFields = [new MetadataV2Field { Name = "globalid", Type = MetadataV2FieldType.Uuid }]
        }]
    };
}
