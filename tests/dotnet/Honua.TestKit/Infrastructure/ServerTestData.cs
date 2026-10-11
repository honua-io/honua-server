// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit.Seeding;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Helper methods for seeding server integration test data.
/// </summary>
public static class ServerTestData
{
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
            await using var stream = assembly.GetManifestResourceStream(
                "Honua.Postgres.Migrations.006_AddRasterTileRendererProvenance.sql")
                ?? throw new InvalidOperationException("The raster cache provenance migration was not embedded.");
            using var reader = new StreamReader(stream);
            var ddl = (await reader.ReadToEndAsync().ConfigureAwait(false))
                .Replace("$HonuaSchema$", "honua", StringComparison.Ordinal);
            await fixture.ExecuteAsync(ddl).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static string ResolveSeedPath()
        => RepositoryPaths.Resolve("tests", "seed", "server.yaml");
}
