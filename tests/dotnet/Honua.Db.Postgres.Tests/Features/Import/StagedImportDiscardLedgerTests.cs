// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Db.Postgres.Features.FileImport;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Migration;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

// Discard drops only the server-owned table of a committed staged import for the same tenant,
// requester and intent, refuses a published or caller-named table, and is idempotent.
[Collection("Database")]
public sealed class StagedImportDiscardLedgerTests(PostgresFixture fixture)
{
    private const string Table = "staged_0123456789abcdef01234567";
    private const string PhysicalTable = "imported_" + Table;

    [IntegrationTest]
    public async Task FeatureCopy_MissingStagedTable_CannotReplaceTheReceiptsTarget()
    {
        await WithSchemaAsync(async (schema, ledger) =>
        {
            await SeedCommittedAsync(schema, new SeedTable(Table, PhysicalTable, ImportTableOwnership.Marker));
            await ExecuteAsync($"DROP TABLE \"{schema}\".\"{PhysicalTable}\"; CREATE TABLE \"{schema}\".replacement (id integer); INSERT INTO \"{schema}\".replacement VALUES (42)");
            var writer = new PostgresMigrationCatalogWriter(NullLogger<PostgresMigrationCatalogWriter>.Instance);
            var copy = () => writer.CopyFeatureDataAsync(fixture.ConnectionString, new MigrationFeatureCopyRequest
            {
                SourceSchema = schema,
                SourceTable = "replacement",
                TargetSchema = schema,
                TargetTable = PhysicalTable,
            });

            await copy.Should().ThrowAsync<ImportTableOwnershipException>();

            (await ExistsAsync(schema, PhysicalTable)).Should().BeFalse();
            (await ledger.DiscardStagedAsync(Request(schema), CancellationToken.None))
                .Should().Be(StagedImportDiscardOutcome.Discarded);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var read = new NpgsqlCommand($"SELECT id FROM \"{schema}\".replacement", connection);
            (await read.ExecuteScalarAsync()).Should().Be(42);
        });
    }

    [IntegrationTest]
    public async Task DiscardStagedAsync_OwnedCommittedImport_DropsTableAndSpendsReceipt()
    {
        await WithSchemaAsync(async (schema, ledger) =>
        {
            await SeedCommittedAsync(schema, new SeedTable(Table, PhysicalTable, ImportTableOwnership.Marker));

            var outcome = await ledger.DiscardStagedAsync(Request(schema), CancellationToken.None);

            outcome.Should().Be(StagedImportDiscardOutcome.Discarded);
            (await ExistsAsync(schema, PhysicalTable)).Should().BeFalse();
            (await SpatialFactsCountAsync(schema)).Should().Be(0);
            (await ReceiptCountAsync(schema)).Should().Be(1, "the spent receipt is kept as a tombstone");
            (await ReceiptFingerprintAsync(schema)).Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("staged-import-discarded/v1\n" + ScopeKey("tenant-a")))));
        });
    }

    [IntegrationTest]
    public async Task DiscardStagedAsync_ThenRetriedOriginalImport_ConflictsInsteadOfRecreating()
    {
        await WithSchemaAsync(async (schema, ledger) =>
        {
            await SeedCommittedAsync(schema, new SeedTable(Table, PhysicalTable, ImportTableOwnership.Marker));
            await ledger.DiscardStagedAsync(Request(schema), CancellationToken.None);
            var imported = false;

            var retry = () => ledger.GetOrImportAsync(
                new ServerJobArtifactImportScope("tenant-a", "actor-1", "intent-1", new string('f', 64), schema, Table),
                (_, _) => { imported = true; throw new InvalidOperationException("must not import"); },
                CancellationToken.None);

            await retry.Should().ThrowAsync<ServerJobArtifactImportConflictException>();
            imported.Should().BeFalse();
            (await ExistsAsync(schema, PhysicalTable)).Should().BeFalse();
        });
    }

    [IntegrationTest]
    public async Task DiscardStagedAsync_TableAlreadyGone_SpendsTheReceipt()
    {
        await WithSchemaAsync(async (schema, ledger) =>
        {
            await SeedCommittedAsync(schema, new SeedTable(Table, PhysicalTable, ImportTableOwnership.Marker));
            await ExecuteAsync($"DROP TABLE \"{schema}\".\"{PhysicalTable}\"");

            (await ledger.DiscardStagedAsync(Request(schema), CancellationToken.None))
                .Should().Be(StagedImportDiscardOutcome.Discarded);
            (await ledger.DiscardStagedAsync(Request(schema), CancellationToken.None))
                .Should().Be(StagedImportDiscardOutcome.NotFound);
        });
    }

    [IntegrationTest]
    public async Task DiscardStagedAsync_ReservedReceipt_FindsNothing()
    {
        await WithSchemaAsync(async (schema, ledger) =>
        {
            await SeedCommittedAsync(schema, new SeedTable(Table, PhysicalTable, ImportTableOwnership.Marker));
            await ExecuteAsync($"UPDATE \"{schema}\".job_artifact_import_receipt SET phase = 'reserved'");

            (await ledger.DiscardStagedAsync(Request(schema), CancellationToken.None))
                .Should().Be(StagedImportDiscardOutcome.NotFound);
            (await ExistsAsync(schema, PhysicalTable)).Should().BeTrue();
        });
    }

    [IntegrationTest]
    public async Task DiscardStagedAsync_WaitsForTheTableLock_ThenRefusesALayerPublishedMeanwhile()
    {
        await WithSchemaAsync(async (schema, ledger) =>
        {
            await SeedCommittedAsync(schema, new SeedTable(Table, PhysicalTable, ImportTableOwnership.Marker));
            await using var publisher = await fixture.DataSource.OpenConnectionAsync();
            await using var publication = await publisher.BeginTransactionAsync();
            await using (var hold = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", publisher, publication))
            {
                hold.Parameters.AddWithValue("key", $"honua.file-import.replace:{schema}.{PhysicalTable}");
                await hold.ExecuteNonQueryAsync();
            }

            var discard = ledger.DiscardStagedAsync(Request(schema), CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            discard.IsCompleted.Should().BeFalse("discard serializes with publication of the same table");
            await using (var layer = new NpgsqlCommand(
                $"INSERT INTO \"{schema}\".layers (table_schema, table_name) VALUES (@schema, @table)", publisher, publication))
            {
                layer.Parameters.AddWithValue("schema", schema);
                layer.Parameters.AddWithValue("table", PhysicalTable);
                await layer.ExecuteNonQueryAsync();
            }
            await publication.CommitAsync();

            (await discard).Should().Be(StagedImportDiscardOutcome.Referenced);
            (await ExistsAsync(schema, PhysicalTable)).Should().BeTrue();
        });
    }

    [IntegrationTest]
    public async Task DiscardStagedAsync_AnotherTenantsReceiptForTheSameTable_IsLeftAlone()
    {
        await WithSchemaAsync(async (schema, ledger) =>
        {
            await SeedCommittedAsync(schema, new SeedTable(Table, PhysicalTable, ImportTableOwnership.Marker), tenantId: "tenant-b");

            (await ledger.DiscardStagedAsync(Request(schema), CancellationToken.None))
                .Should().Be(StagedImportDiscardOutcome.NotFound);
            (await ExistsAsync(schema, PhysicalTable)).Should().BeTrue();
            (await ReceiptFingerprintAsync(schema)).Should().Be(new string('f', 64));
        });
    }

    [IntegrationTest]
    public async Task DiscardStagedAsync_Repeated_IsIdempotent()
    {
        await WithSchemaAsync(async (schema, ledger) =>
        {
            await SeedCommittedAsync(schema, new SeedTable(Table, PhysicalTable, ImportTableOwnership.Marker));
            await ledger.DiscardStagedAsync(Request(schema), CancellationToken.None);

            var again = await ledger.DiscardStagedAsync(Request(schema), CancellationToken.None);

            again.Should().Be(StagedImportDiscardOutcome.NotFound);
        });
    }

    [IntegrationTest]
    public async Task DiscardStagedAsync_PublishedTable_IsRefusedAndKept()
    {
        await WithSchemaAsync(async (schema, ledger) =>
        {
            await SeedCommittedAsync(schema, new SeedTable(Table, PhysicalTable, ImportTableOwnership.Marker));
            await ExecuteAsync($"INSERT INTO \"{schema}\".layers (table_schema, table_name) VALUES ('{schema}', '{PhysicalTable}')");

            var outcome = await ledger.DiscardStagedAsync(Request(schema), CancellationToken.None);

            outcome.Should().Be(StagedImportDiscardOutcome.Referenced);
            (await ExistsAsync(schema, PhysicalTable)).Should().BeTrue();
            (await ReceiptCountAsync(schema)).Should().Be(1);
        });
    }

    [IntegrationTest]
    public async Task DiscardStagedAsync_DependentView_IsRefusedAndKept()
    {
        await WithSchemaAsync(async (schema, ledger) =>
        {
            await SeedCommittedAsync(schema, new SeedTable(Table, PhysicalTable, ImportTableOwnership.Marker));
            await ExecuteAsync($"CREATE VIEW \"{schema}\".depends AS SELECT id FROM \"{schema}\".\"{PhysicalTable}\"");

            var outcome = await ledger.DiscardStagedAsync(Request(schema), CancellationToken.None);

            outcome.Should().Be(StagedImportDiscardOutcome.Referenced);
            (await ExistsAsync(schema, PhysicalTable)).Should().BeTrue();
            (await ReceiptCountAsync(schema)).Should().Be(1);
        });
    }

    [IntegrationTheory]
    [InlineData("caller-named")]
    [InlineData("unmarked")]
    public async Task DiscardStagedAsync_TableNotServerOwned_IsRefusedAndKept(string variant)
    {
        await WithSchemaAsync(async (schema, ledger) =>
        {
            var table = variant == "caller-named" ? "parcels" : Table;
            var physical = "imported_" + table;
            await SeedCommittedAsync(schema, new SeedTable(table, physical, variant == "unmarked" ? null : ImportTableOwnership.Marker));

            var outcome = await ledger.DiscardStagedAsync(Request(schema), CancellationToken.None);

            outcome.Should().Be(variant == "caller-named" ? StagedImportDiscardOutcome.NotFound : StagedImportDiscardOutcome.NotOwned);
            (await ExistsAsync(schema, physical)).Should().BeTrue();
            (await ReceiptCountAsync(schema)).Should().Be(1);
        });
    }

    [IntegrationTheory]
    [InlineData("requester")]
    [InlineData("intent")]
    public async Task DiscardStagedAsync_AnotherRequesterOrIntent_FindsNothingAndKeepsTable(string change)
    {
        await WithSchemaAsync(async (schema, ledger) =>
        {
            await SeedCommittedAsync(schema, new SeedTable(Table, PhysicalTable, ImportTableOwnership.Marker));
            var request = change == "requester"
                ? Request(schema) with { ActorId = "actor-2" }
                : Request(schema) with { IntentId = "intent-other" };

            (await ledger.DiscardStagedAsync(request, CancellationToken.None)).Should().Be(StagedImportDiscardOutcome.NotFound);
            (await ExistsAsync(schema, PhysicalTable)).Should().BeTrue();
            (await ReceiptFingerprintAsync(schema)).Should().Be(new string('f', 64));
        });
    }

    private static string ScopeKey(string tenantId)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(tenantId + "\nactor-1\nintent-1")));

    private static StagedImportDiscardRequest Request(string schema)
        => new("tenant-a", "actor-1", "intent-1", schema, Table);

    private async Task WithSchemaAsync(Func<string, PostgresServerJobArtifactImportLedger, Task> body)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("staged_discard");
        try
        {
            await ApplyMigrationAsync("138_CreateJobArtifactImportReceipt.sql", schema);
            await ExecuteAsync($"CREATE TABLE \"{schema}\".layers (table_schema text, table_name text)");
            await ApplyMigrationAsync("141_CreateImportedDatasetSpatialMetadata.sql", schema);
            var ledger = new PostgresServerJobArtifactImportLedger(
                fixture.DataSource, new PostgresSchemaConfiguration(schema, schema, [schema]));
            await body(schema, ledger);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private sealed record SeedTable(string Logical, string Physical, string? Marker);

    private async Task SeedCommittedAsync(string schema, SeedTable target, string tenantId = "tenant-a")
    {
        await ExecuteAsync($"CREATE TABLE \"{schema}\".\"{target.Physical}\" (id SERIAL PRIMARY KEY, properties JSONB)");
        if (target.Marker is not null)
        {
            await ExecuteAsync(ImportTableOwnership.MarkSql(schema, target.Physical));
        }

        var scopeKey = ScopeKey(tenantId);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var insert = new NpgsqlCommand(
            $"""
            INSERT INTO "{schema}".job_artifact_import_receipt
                (scope_key, tenant_id, actor_id, intent_id, fingerprint, phase, schema_name, table_name,
                 physical_table, feature_count, source_srid, target_srid, content_sha256, crs_state)
            VALUES (@scope_key, @tenant, 'actor-1', 'intent-1', @fingerprint, 'committed', @schema, @table,
                    @physical, 1, 4326, 4326, @content, 'known')
            """, connection);
        insert.Parameters.AddWithValue("scope_key", scopeKey);
        insert.Parameters.AddWithValue("tenant", tenantId);
        insert.Parameters.AddWithValue("fingerprint", new string('f', 64));
        insert.Parameters.AddWithValue("schema", schema);
        insert.Parameters.AddWithValue("table", target.Logical);
        insert.Parameters.AddWithValue("physical", target.Physical);
        insert.Parameters.AddWithValue("content", new string('c', 64));
        await insert.ExecuteNonQueryAsync();
        await ExecuteAsync($"""
            INSERT INTO "{schema}".imported_dataset_spatial_metadata
                (schema_name, table_name, geometry_column, relation_oid, geometry_attnum, storage_srid)
            VALUES ('{schema}', '{target.Physical}', 'geometry', '"{schema}"."{target.Physical}"'::regclass, 1, 4326)
            """);
    }

    private async Task<bool> ExistsAsync(string schema, string table)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass(@relation) IS NOT NULL", connection);
        command.Parameters.AddWithValue("relation", $"\"{schema}\".\"{table}\"");
        return await command.ExecuteScalarAsync() is true;
    }

    private async Task ApplyMigrationAsync(string file, string schema)
    {
        var script = await File.ReadAllTextAsync(RepositoryPaths.Resolve("src", "Honua.Server", "Migrations", file));
        await ExecuteAsync(script.Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal));
    }

    private async Task<string> ReceiptFingerprintAsync(string schema)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT fingerprint FROM \"{schema}\".job_artifact_import_receipt", connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> SpatialFactsCountAsync(string schema)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{schema}\".imported_dataset_spatial_metadata", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> ReceiptCountAsync(string schema)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{schema}\".job_artifact_import_receipt", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
