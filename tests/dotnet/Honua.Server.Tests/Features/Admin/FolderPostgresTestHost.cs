// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Db.Postgres.Features.Metadata;
using Honua.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

internal static class FolderPostgresTestHost
{
    public static void Configure(IServiceCollection services, Func<string?> schema)
    {
        services.RemoveAll<IMetadataV2GraphProvider>();
        services.RemoveAll<IMetadataV2GraphStore>();
        services.AddScoped<IMetadataV2GraphStore>(provider => new PostgresMetadataV2GraphStore(
            provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(), "Test",
            provider.GetRequiredService<IDatabaseSchemaGuard>(), schema(),
            provider.GetRequiredService<IMetadataV2GraphCacheInvalidator>()));
        services.AddScoped<IMetadataV2GraphProvider>(provider => provider.GetRequiredService<IMetadataV2GraphStore>());
    }

    public static async Task InstallSchemaAsync(WebAppFixture fixture)
    {
        var assembly = typeof(Program).Assembly;
        foreach (var suffix in new[] { "031_CreateMetadataV2Snapshot.sql", "051_RelaxMetadataV2ServiceNameIndex.sql" })
        {
            var name = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith(suffix, StringComparison.Ordinal));
            await using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var ddl = (await reader.ReadToEndAsync()).Replace("$HonuaSchema$", $"\"{fixture.CurrentSchema}\"", StringComparison.Ordinal);
            await fixture.Postgres.ExecuteDdlUnderLockAsync(ddl);
        }
    }
}
