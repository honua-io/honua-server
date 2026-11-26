// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Server.Startup;
using Honua.TestKit;
using Honua.TestKit.Helpers;
using Honua.TestKit.Mixins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Infrastructure;

internal static class MigratedPostgresTestHost
{
    public static WebApplicationFactory<Program> Create(string connectionString)
        => Build(connectionString, configureExtraServices: null);

    public static WebApplicationFactory<Program> CreateLicensed(string connectionString)
        => Build(connectionString, services =>
        {
            var license = new TestLicenseEntitlementService(HonuaEdition.Pro);
            services.RemoveAll<ILicenseEntitlementService>();
            services.RemoveAll<ILicenseStatusProvider>();
            services.AddSingleton<ILicenseEntitlementService>(license);
            services.AddSingleton<ILicenseStatusProvider>(license);
        });

    private static WebApplicationFactory<Program> Build(
        string connectionString,
        Action<IServiceCollection>? configureExtraServices)
        => ConfiguredWebApplicationFactory.Create(
            builder =>
            {
                builder.UseSetting("HONUA_SKIP_MIGRATIONS", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting("HONUA_REGISTER_TEST_INFRASTRUCTURE", "true");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(
                        WebAppFixturePostgresWiringMixin.BuildAppConfigurationDictionary(
                            connectionString,
                            new Dictionary<string, string?>
                            {
                                ["HONUA_ADMIN_PASSWORD"] = WebAppFixture.SharedAdminPassword,
                                ["HONUA_SKIP_MIGRATIONS"] = "false"
                            })));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IDatabaseMigrationRunner>();
                    services.AddSingleton<IDatabaseMigrationRunner>(new PostgresDatabaseMigrationRunner(
                        new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest),
                        ServerCoreSchemaMigrations.Manifest));
                    configureExtraServices?.Invoke(services);
                });
            },
            "Test");
}
