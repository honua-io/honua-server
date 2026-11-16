// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Metadata;

public sealed partial class PostgresMetadataV2GraphStoreFreshDbTests
{
    private static MetadataV2Graph CreateGlobalIdBinding(string schema, string bindingKind, string? ignoredOption)
    {
        var graph = GlobalIdGraph(schema);
        if (bindingKind == "source")
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
            graph = graph with
            {
                StorageBindings = [graph.StorageBindings[0] with
            {
                Options = new Dictionary<string, JsonElement>(graph.StorageBindings[0].Options)
                { [ignoredOption] = JsonSerializer.SerializeToElement("other_column") }
            }]
            };
        }
        return graph;
    }

    private static async Task AssertGlobalIdRowsConstrainedAsync(string schema, NpgsqlCommand command)
    {
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
    }

    private async Task AssertConcurrentGlobalIdInsertRejectedAsync(string schema, NpgsqlConnection connection, NpgsqlCommand command)
    {
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

    private async Task AssertExplicitRepairEnablesMigrationAsync(string schema, NpgsqlCommand command)
    {
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

    private static MetadataV2Graph CreateSeparateSchemaGlobalIdGraph(string featureSchema, int optionCase)
    {
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
        return graph;
    }

    private async Task CreateRowSecuredGlobalIdTableAsync(string schema, string role)
    {
        await fixture.ExecuteAsync($$"""
            CREATE TABLE "{{schema}}".features (layer_id integer NOT NULL, attributes jsonb NOT NULL);
            CREATE ROLE {{role}};
            GRANT USAGE ON SCHEMA "{{schema}}" TO {{role}};
            GRANT SELECT, INSERT ON "{{schema}}".features TO {{role}};
            ALTER TABLE "{{schema}}".features ENABLE ROW LEVEL SECURITY;
            CREATE POLICY visible_rows ON "{{schema}}".features FOR SELECT USING (false);
            CREATE POLICY writable_rows ON "{{schema}}".features FOR INSERT WITH CHECK (true);
            """);
    }

    private static MetadataV2Graph CreateExternalGlobalIdGraph((string Managed, string Source) schemas, string connectionKind, string? sourceBackedText)
    {
        var graph = GlobalIdGraph(connectionKind == "external" ? schemas.Managed : schemas.Source);
        graph = graph with
        {
            StorageBindings = [graph.StorageBindings[0] with
            {
                ConnectionId = connectionKind == "external" ? "conn" : null,
                Options = new Dictionary<string, JsonElement>
                {
                    ["sourceBacked"] = sourceBackedText is null
                        ? JsonSerializer.SerializeToElement(true)
                        : JsonSerializer.SerializeToElement(sourceBackedText),
                    ["schemaName"] = JsonSerializer.SerializeToElement(connectionKind == "external" ? schemas.Managed : schemas.Source),
                    ["tableName"] = JsonSerializer.SerializeToElement("features"),
                    ["attributesColumn"] = JsonSerializer.SerializeToElement("attributes"),
                    ["layerDiscriminatorColumn"] = JsonSerializer.SerializeToElement("layer_id")
                }
            }]
        };
        return graph;
    }

    private static async Task InsertDuplicateGlobalIdsAsync(string schema, NpgsqlCommand command)
    {
        command.CommandText = $$"""
            INSERT INTO "{{schema}}".features VALUES
                (7, '{"globalid":"6f1c2d3e-4b5a-4c6d-8e7f-0a1b2c3d4e5f"}'),
                (7, '{"globalid":"{6F1c2D3e-4B5a-4C6d-8E7f-0A1b2C3d4E5f}"}')
            """;
        await command.ExecuteNonQueryAsync();
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
