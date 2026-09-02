// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Db.Postgres.Tests;

/// <summary>
/// Applies canonical numbered migration SQL to an isolated test schema. Production stores must
/// never provision these objects; tests that skip DbUp own their schema setup explicitly.
/// </summary>
internal static class CoreMigrationTestFixture
{
    public static Task ApplyMetadataV2Async(PostgresFixture fixture, string schema) =>
        ApplyAsync(
            fixture,
            schema,
            "src", "Honua.Server", "Migrations", "031_CreateMetadataV2Snapshot.sql");

    public static Task ApplyRasterLayerStatisticsAsync(PostgresFixture fixture, string schema) =>
        ApplyAsync(
            fixture,
            schema,
            "src", "Honua.Db", "Postgres", "Migrations", "003_CreateRasterLayerStatistics.sql");

    public static Task ApplyRasterExternalStorageAsync(PostgresFixture fixture, string schema) =>
        ApplyAsync(
            fixture,
            schema,
            "src", "Honua.Server", "Migrations", "055_SetRasterDataExternalStorage.sql");

    private static async Task ApplyAsync(
        PostgresFixture fixture,
        string schema,
        params string[] migrationPath)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        var quotedSchema = $"\"{schema.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        var sql = (await File.ReadAllTextAsync(RepositoryPaths.Resolve(migrationPath)))
            .Replace("honua.", $"{quotedSchema}.", StringComparison.Ordinal);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
