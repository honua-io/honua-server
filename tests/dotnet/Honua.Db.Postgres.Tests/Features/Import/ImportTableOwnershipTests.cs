// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Db.Postgres.Features.Geoprocessing;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

[Collection("Database")]
public sealed class ImportTableOwnershipTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("create_import_table", "target")]
    [InlineData("create_import_staging_table", "target")]
    [InlineData("create_import_staging_table", "target__staging")]
    [InlineData("drop_import_staging_table", "target__staging")]
    [InlineData("swap_import_table", "target")]
    [InlineData("swap_import_table", "target__staging")]
    public async Task ImportHelpers_UnownedRelation_RefuseWithoutChangingRows(string function, string table)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ownership");
        try
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await InstallHelpersAsync(connection, schema);
            await ExecuteAsync(connection, $"CREATE TABLE \"{schema}\".\"{table}\" (sentinel text); INSERT INTO \"{schema}\".\"{table}\" VALUES ('original')");
            var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
                $"SELECT \"{schema}\".{function}('{schema}', 'target')"));
            Assert.Equal("HN409", error.SqlState);
            await using var verify = new NpgsqlCommand($"SELECT sentinel FROM \"{schema}\".\"{table}\"", connection);
            Assert.Equal("original", await verify.ExecuteScalarAsync());
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task SwapImportTable_TargetCreatedAfterStaging_PreservesCompetingTable()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ownership_race");
        try
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await InstallHelpersAsync(connection, schema);
            await ExecuteAsync(connection, $"SELECT \"{schema}\".create_import_staging_table('{schema}', 'target')");
            await using (var competing = await fixture.DataSource.OpenConnectionAsync())
            {
                await ExecuteAsync(competing, $"CREATE TABLE \"{schema}\".target (sentinel text); INSERT INTO \"{schema}\".target VALUES ('competing owner')");
            }
            var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
                $"SELECT \"{schema}\".swap_import_table('{schema}', 'target')"));
            Assert.Equal("HN409", error.SqlState);
            await using var verify = new NpgsqlCommand($"SELECT sentinel FROM \"{schema}\".target", connection);
            Assert.Equal("competing owner", await verify.ExecuteScalarAsync());
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task ImportHelpers_OwnedAndExplicitlyAdoptedLegacyTarget_ReplaceAndRetainMarker()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ownership_adopt");
        try
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await InstallHelpersAsync(connection, schema);
            await ExecuteAsync(connection, $"CREATE TABLE \"{schema}\".target (old_value text); " + ImportTableOwnership.MarkSql(schema, "target"));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await ExecuteAsync(connection, $"SELECT \"{schema}\".create_import_staging_table('{schema}', 'target'); INSERT INTO \"{schema}\".target__staging (properties) VALUES ('{{\"attempt\":{attempt}}}'); SELECT \"{schema}\".swap_import_table('{schema}', 'target')");
                await using var verify = new NpgsqlCommand($"SELECT properties->>'attempt' FROM \"{schema}\".target", connection);
                Assert.Equal(attempt.ToString(System.Globalization.CultureInfo.InvariantCulture), await verify.ExecuteScalarAsync());
                await using var transaction = await connection.BeginTransactionAsync();
                await ImportTableOwnership.AssertOwnedAsync(connection, schema, "target", CancellationToken.None);
                await transaction.CommitAsync();
            }
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task SwapImportTable_LongOwnedTarget_RetainsDistinctCanonicalIndexesAcrossReplacements()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ownership_long");
        var target = new string('t', 63);
        try
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await InstallHelpersAsync(connection, schema);
            await ExecuteAsync(connection, $"CREATE TABLE \"{schema}\".\"{target}\" (old_value text); " + ImportTableOwnership.MarkSql(schema, target));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await using var stageCommand = new NpgsqlCommand($"SELECT \"{schema}\".create_import_staging_table('{schema}', '{target}')", connection);
                var staging = Assert.IsType<string>(await stageCommand.ExecuteScalarAsync());
                await ExecuteAsync(connection, $"INSERT INTO \"{schema}\".\"{staging}\" (properties) VALUES ('{{\"attempt\":{attempt}}}'); SELECT \"{schema}\".swap_import_table('{schema}', '{target}')");
                await using var verifyRows = new NpgsqlCommand($"SELECT properties->>'attempt' FROM \"{schema}\".\"{target}\"", connection);
                Assert.Equal(attempt.ToString(System.Globalization.CultureInfo.InvariantCulture), await verifyRows.ExecuteScalarAsync());
                await using var verifyIndexes = new NpgsqlCommand($"""
                    SELECT count(*) FROM pg_indexes
                    WHERE schemaname = @schema AND tablename = @target
                      AND ((indexname = "{schema}".import_index_name(@target, 'geometry') AND indexdef LIKE '%USING gist (geometry)%')
                        OR (indexname = "{schema}".import_index_name(@target, 'properties') AND indexdef LIKE '%USING gin (properties)%'))
                    """, connection);
                verifyIndexes.Parameters.AddWithValue("schema", schema);
                verifyIndexes.Parameters.AddWithValue("target", target);
                Assert.Equal(2L, await verifyIndexes.ExecuteScalarAsync());
            }
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task EnsureImportTable_ExistingUnownedTable_DoesNotAdoptIt()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ownership_append");
        try
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await InstallHelpersAsync(connection, schema);
            await ExecuteAsync(connection, $"CREATE TABLE \"{schema}\".target (sentinel text); SELECT \"{schema}\".ensure_import_table('{schema}', 'target')");
            await using var transaction = await connection.BeginTransactionAsync();
            await Assert.ThrowsAsync<ImportTableOwnershipException>(() => ImportTableOwnership.AssertOwnedAsync(connection, schema, "target", CancellationToken.None));
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task LayerSink_UnownedPublishedTable_AllowsAppendAndUpsertButRefusesReplacement()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ownership_sink");
        try
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await ExecuteAsync(connection, $$"""
                CREATE TABLE "{{schema}}".target (id BIGSERIAL PRIMARY KEY, geometry geometry(Geometry, 4326), attributes JSONB NOT NULL);
                INSERT INTO "{{schema}}".target (attributes) VALUES
                    ('{"key":"old","value":"original"}'), ('{"key":"sentinel","value":"untouched"}');
                """);
            await using var geometry = new NpgsqlCommand("SELECT ST_AsBinary(ST_Point(1, 2))", connection);
            var wkb = Assert.IsType<byte[]>(await geometry.ExecuteScalarAsync());
            var sink = new PostgresHonuaLayerSink(fixture.DataSource);
            var request = new HonuaLayerSinkRequest(schema, "target", "geometry", 4326,
                HonuaLayerLoadMode.Append, "append", ["key"]);
            await sink.LoadAsync(request, [new(wkb, """{"key":"new","value":"appended"}""")], CancellationToken.None);
            await sink.LoadAsync(request with { LoadMode = HonuaLayerLoadMode.Upsert, BatchId = "upsert" },
                [new(wkb, """{"key":"new","value":"updated"}""")], CancellationToken.None);

            await using var verify = new NpgsqlCommand($"SELECT string_agg(attributes->>'value', ',' ORDER BY attributes->>'key') FROM \"{schema}\".target", connection);
            Assert.Equal("updated,original,untouched", await verify.ExecuteScalarAsync());
            await using var marker = new NpgsqlCommand($"SELECT obj_description('\"{schema}\".target'::regclass, 'pg_class')", connection);
            Assert.Equal(DBNull.Value, await marker.ExecuteScalarAsync());

            var replace = request with { LoadMode = HonuaLayerLoadMode.Replace, BatchId = "replace" };
            HonuaLayerSinkRow[] replacement = [new(wkb, """{"key":"new","value":"replacement"}""")];
            await Assert.ThrowsAsync<ImportTableOwnershipException>(() => sink.LoadAsync(replace, replacement, CancellationToken.None));
            Assert.Equal("updated,original,untouched", await verify.ExecuteScalarAsync());
            await ExecuteAsync(connection, ImportTableOwnership.MarkSql(schema, "target"));
            await sink.LoadAsync(replace, replacement, CancellationToken.None);
            Assert.Equal("replacement", await verify.ExecuteScalarAsync());
            Assert.Equal("honua:import-owned:v1", await marker.ExecuteScalarAsync());
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task RequiredOwnedTable_AbsentStaging_RefusesBeforeDeletingOwnedTarget()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ownership_missing");
        try
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await InstallHelpersAsync(connection, schema);
            await ExecuteAsync(connection, $"CREATE TABLE \"{schema}\".target (sentinel text); INSERT INTO \"{schema}\".target VALUES ('original'); "
                + ImportTableOwnership.MarkSql(schema, "target"));
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await Assert.ThrowsAsync<ImportTableOwnershipException>(() =>
                    ImportTableOwnership.AssertExistingOwnedAsync(connection, schema, "target__staging", CancellationToken.None));
            }
            var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
                $"SELECT \"{schema}\".swap_import_table('{schema}', 'target')"));
            Assert.Equal("HN409", error.SqlState);
            await using var verify = new NpgsqlCommand($"SELECT sentinel FROM \"{schema}\".target", connection);
            Assert.Equal("original", await verify.ExecuteScalarAsync());
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InstallHelpersAsync(NpgsqlConnection connection, string schema)
    {
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null && !Directory.Exists(Path.Join(root.FullName, "src", "Honua.Server", "Migrations")))
        {
            root = root.Parent;
        }
        Assert.NotNull(root);
        foreach (var file in new[] { "070_AddImportLoadModes.sql", "071_FixImportStagingTableNameLength.sql", "108_FixLongImportIndexNames.sql", "129_ProtectImportTableOwnership.sql" })
        {
            var sql = (await File.ReadAllTextAsync(Path.Join(root.FullName, "src", "Honua.Server", "Migrations", file)))
                .Replace("honua.", $"\"{schema}\".", StringComparison.Ordinal)
                .Replace("CREATE SCHEMA IF NOT EXISTS honua;", "", StringComparison.Ordinal)
                .Replace("CREATE SCHEMA IF NOT EXISTS honua_data;", "", StringComparison.Ordinal);
            await ExecuteAsync(connection, sql);
        }
    }
}
