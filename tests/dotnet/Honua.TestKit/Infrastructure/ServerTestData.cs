// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit.Seeding;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Helper methods for seeding server integration test data.
/// </summary>
public static class ServerTestData
{
    private static readonly string[] _fixtureAttachmentMigrations =
        ["002_CreateAttachmentsTable.sql", "125_AddImportedAttachmentIdentity.sql"];

    private static readonly string[] _rasterSchemaMigrations =
    [
        "Honua.Postgres.Migrations.006_AddRasterTileRendererProvenance.sql",
        "Honua.Postgres.Migrations.007_AddRasterSecondBandAlpha.sql",
    ];

    /// <summary>
    /// Seeds the fixture and applies the production cache provenance DDL to its fixed raster schema.
    /// </summary>
    public static async Task SeedAsync(PostgresFixture fixture, string schemaName)
    {
        await fixture.ApplySeedAsync(ResolveSeedPath(), schemaName).ConfigureAwait(false);

        // server.yaml owns fixed honua raster tables, while test hosts skip application migrations.
        // Read the owning migration instead of maintaining a divergent copy of its column definitions.
        await fixture.RunUnderSchemaMutationLockAsync(async () =>
        {
            var assembly = typeof(PostgresRasterStore).Assembly;
            foreach (var migration in _rasterSchemaMigrations)
            {
                await using var stream = assembly.GetManifestResourceStream(migration)
                    ?? throw new InvalidOperationException($"The raster migration {migration} was not embedded.");
                using var reader = new StreamReader(stream);
                var ddl = (await reader.ReadToEndAsync().ConfigureAwait(false))
                    .Replace("$HonuaSchema$", "honua", StringComparison.Ordinal);
                await fixture.ExecuteAsync(ddl).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Provisions the production-owned attachment tables in the fixture schema.
    /// YAML catalog SQL targets literal honua tables, while feature rows and their
    /// transactional attachment cleanup belong to the isolated fixture schema.
    /// </summary>
    internal static Task EnsureFixtureAttachmentsAsync(PostgresFixture fixture, string schemaName)
    {
        var quotedSchema = SchemaSearchPath.ValidateAndQuote(schemaName);
        return fixture.RunUnderSchemaMutationLockAsync(async () =>
        {
            await using var connection = await fixture.GetConnectionAsync(schemaName).ConfigureAwait(false);
            foreach (var migration in _fixtureAttachmentMigrations)
            {
                await using var stream = typeof(PostgresFixture).Assembly.GetManifestResourceStream(
                    $"Honua.Server.Migrations.{migration}")
                    ?? throw new InvalidOperationException($"The attachment migration {migration} was not embedded.");
                using var reader = new StreamReader(stream);
                var ddl = (await reader.ReadToEndAsync().ConfigureAwait(false))
                    .Replace("honua.", $"{quotedSchema}.", StringComparison.Ordinal)
                    .Replace("$HonuaSchema$", quotedSchema, StringComparison.Ordinal);
                await using var command = connection.CreateCommand();
                command.CommandText = ddl;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        });
    }

    private static string ResolveSeedPath()
        => RepositoryPaths.Resolve("tests", "seed", "server.yaml");
}
