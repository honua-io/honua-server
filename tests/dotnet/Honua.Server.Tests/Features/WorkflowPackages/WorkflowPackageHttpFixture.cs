// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Db.Postgres.Features.WorkflowPackages;
using Honua.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Honua.Server.Tests.Features.WorkflowPackages;

internal static class WorkflowPackageHttpFixture
{
    public static void Configure(WebAppFixture fixture, Func<NpgsqlTransaction, CancellationToken, Task>? publicationCommit = null)
        => fixture.ConfigureServices(services =>
        {
            services.RemoveAll<IWorkflowPackageStore>();
            services.AddScoped<IWorkflowPackageStore>(provider => new PostgresWorkflowPackageStore(
                provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(), fixture.CurrentSchema, publicationCommit));
        });

    public static async Task InitializeSchemaAsync(WebAppFixture fixture)
    {
        var schema = fixture.CurrentSchema ?? throw new InvalidOperationException("An isolated fixture schema is required.");
        foreach (var name in new[] { "122_CreateWorkflowPackages.sql", "133_AddWorkflowPackageOwnership.sql" })
        {
            await using var resource = typeof(PostgresFixture).Assembly.GetManifestResourceStream("Honua.Server.Migrations." + name)
                ?? throw new InvalidOperationException("Owning workflow migration is unavailable.");
            using var reader = new StreamReader(resource);
            var ddl = (await reader.ReadToEndAsync()).Replace("$HonuaSchema$", "\"" + schema.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"", StringComparison.Ordinal);
            await fixture.Postgres.ExecuteDdlUnderLockAsync(ddl, schema);
        }
    }
}
