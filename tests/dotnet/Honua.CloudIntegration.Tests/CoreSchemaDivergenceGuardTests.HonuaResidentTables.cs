// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.AnalysisContent.Abstractions;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Server.Startup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Honua.CloudIntegration.Tests;

public sealed partial class CoreSchemaDivergenceGuardTests
{
    [SkippableFact]
    public async Task ConfiguredSchema_HonuaResidentTables_MigrateInHonuaAndServeAnalysisContent()
    {
        Skip.IfNot(postgres.Available, "Real PostgreSQL is required for configured-schema migration proof.");
        const string schema = "resident_configured";
        var connectionString = await postgres.CreateFreshDatabaseAsync(enablePostGis: true);
        var configuration = ResidentTableConfiguration(connectionString, schema);
        var runner = new PostgresDatabaseMigrationRunner(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest, configuration),
            ServerCoreSchemaMigrations.Manifest,
            configuration: configuration);

        var result = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);

        result.Successful.Should().BeTrue(result.Error?.ToString());
        (await CountAsync(connectionString, """
            SELECT count(*) FROM pg_catalog.pg_trigger
            WHERE tgname = 'trigger_notify_feature_changes' AND tgrelid = 'honua.feature_changes'::regclass
            """)).Should().Be(1L);
        (await CountAsync(connectionString, """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'honua' AND table_name = 'analysis_content_items' AND column_name = 'tenant_id'
            """)).Should().Be(1L);
        await using var provider = CreateAttachmentRegistrationProvider(configuration);
        await using var scope = provider.CreateAsyncScope();
        var page = await scope.ServiceProvider.GetRequiredService<IAnalysisContentStore>()
            .ListItemsAsync(new AnalysisContentItemQuery { Limit = 1, Offset = 0 });
        page.TotalCount.Should().Be(0);
    }

    private static IConfiguration ResidentTableConfiguration(string connectionString, string schema)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connectionString,
            ["Database:Schema"] = schema,
            ["Security:ConnectionEncryption:MasterKey"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            ["FileStorage:Provider"] = "Local",
            ["FileStorage:LocalStorage:BasePath"] = Path.Join(Path.GetTempPath(), "honua-resident-" + Guid.NewGuid().ToString("N")),
            ["FileStorage:EnableAutomaticCleanup"] = "false"
        }).Build();

    private static async Task<long> CountAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
