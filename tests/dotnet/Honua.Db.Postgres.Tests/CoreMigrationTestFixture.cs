// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Db.Postgres.Tests;

internal static class CoreMigrationTestFixture
{
    public static async Task ApplyMetadataV2Async(PostgresFixture fixture, string schema)
    {
        await ApplyPreVocabularyMetadataV2Async(fixture, schema);
        await ApplyAsync(
            fixture,
            schema,
            "src", "Honua.Server", "Migrations", "186_ExpandMetadataV2Vocabulary.sql");
    }

    public static async Task ApplyPreVocabularyMetadataV2Async(PostgresFixture fixture, string schema)
    {
        await ApplyAsync(
            fixture,
            schema,
            "src", "Honua.Server", "Migrations", "031_CreateMetadataV2Snapshot.sql");
        await ApplyAsync(
            fixture,
            schema,
            "src", "Honua.Server", "Migrations", "155_CreateMetadataFieldHistory.sql");
        await ApplyGlobalIdIntegrityAsync(fixture, schema);
    }

    public static Task ApplyGlobalIdIntegrityAsync(PostgresFixture fixture, string schema) =>
        ApplyAsync(
            fixture,
            schema,
            "src", "Honua.Server", "Migrations", "198_EnforceMetadataGlobalIdUniqueness.sql");

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
            .Replace("$HonuaSchema$", quotedSchema, StringComparison.Ordinal);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
