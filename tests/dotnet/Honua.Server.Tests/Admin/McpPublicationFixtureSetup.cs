// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Startup;
using Honua.TestKit;

namespace Honua.Server.Tests.Admin;

internal static class McpPublicationFixtureSetup
{
    internal static async Task InstallPromotionStoresAsync(WebAppFixture fixture)
    {
        // Test hosts skip DbUp. Readback uses the production promotion stores
        // before falling back to ordinary catalog publications, so install the
        // canonical schema without inserting synthetic promotion records.
        const string migrationName = "Honua.Server.Migrations.082_CreatePromotionStores.sql";
        await using var stream = typeof(ServerCoreSchemaMigrations).Assembly.GetManifestResourceStream(migrationName)
            ?? throw new InvalidOperationException($"Embedded migration '{migrationName}' was not found.");
        using var reader = new StreamReader(stream);
        await fixture.Postgres.ExecuteDdlUnderLockAsync(await reader.ReadToEndAsync());
    }
}
