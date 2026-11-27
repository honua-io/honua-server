// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Geoprocessing.ProcessRelease;
using Honua.Server.Features.Geoprocessing.ProcessRelease;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Geoprocessing;

[Trait("Tier", "Fast")]
public sealed class GpProcessReleaseRegistrationTests
{
    [UnitTest]
    public void OpenAsync_ReturnsNpgsqlConnectionLease_NotUnwrappedConnection()
    {
        var method = typeof(PostgresGpProcessReleaseStore).GetMethod(
            "OpenAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        method!.ReturnType.Should().Be<Task<NpgsqlConnectionLease>>(
            "returning NpgsqlConnection implicitly drops SemaphoreReleasingConnection and leaks concurrency gate slots");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" ", true)]
    [InlineData("postgres", true)]
    [InlineData("postgis", true)]
    [InlineData("postgresql", true)]
    [InlineData("pgsql", true)]
    [InlineData(" PGSQL ", true)]
    [InlineData(" postgis ", true)]
    [InlineData(" POSTGRESQL ", true)]
    [InlineData("mysql", false)]
    [InlineData("mariadb", false)]
    [InlineData(" duckdb ", false)]
    [InlineData(" mssql ", false)]
    [InlineData("redshift", false)]
    public void Registration_ResolvesDurableStoreOnlyForPostgres(string? configuredProvider, bool durable)
    {
        var fallback = new InMemoryGpProcessReleaseStore();
        var services = new ServiceCollection();
        services.AddSingleton<IGpProcessReleaseStore>(fallback);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        using var configuration = new ConfigurationManager();
        configuration["DataSource:Provider"] = configuredProvider;
        configuration["Database:Schema"] = "custom_release_schema";

        services.AddGpProcessReleasePersistence(configuration);
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IGpProcessReleaseStore>();

        if (durable)
        {
            store.Should().BeOfType<PostgresGpProcessReleaseStore>();
        }
        else
        {
            store.Should().BeSameAs(fallback);
        }
    }
}
