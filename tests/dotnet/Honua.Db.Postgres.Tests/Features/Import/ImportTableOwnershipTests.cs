// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
        foreach (var file in new[] { "070_AddImportLoadModes.sql", "071_FixImportStagingTableNameLength.sql", "129_ProtectImportTableOwnership.sql" })
        {
            var sql = (await File.ReadAllTextAsync(Path.Join(root.FullName, "src", "Honua.Server", "Migrations", file)))
                .Replace("honua.", $"\"{schema}\".", StringComparison.Ordinal)
                .Replace("CREATE SCHEMA IF NOT EXISTS honua;", "", StringComparison.Ordinal)
                .Replace("CREATE SCHEMA IF NOT EXISTS honua_data;", "", StringComparison.Ordinal);
            await ExecuteAsync(connection, sql);
        }
    }
}
