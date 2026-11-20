// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Startup;

public sealed class PrimaryCatalogStoreTests
{
    [UnitTheory]
    [InlineData("duckdb", "Production")]
    [InlineData("mysql", "Production")]
    [InlineData("mariadb", "Staging")]
    public void ProductionRefusal_NonPostgreSqlPrimaryOutsideDevelopmentAndTest_NamesTheCatalogAndTheFix(
        string provider,
        string environmentName)
        => PrimaryCatalogStore.ProductionRefusal(Build(provider), HostEnvironment(environmentName))
            .Should().Contain($"'{provider}'")
            .And.Contain("catalog is stored only in a PostgreSQL/PostGIS system database")
            .And.Contain("DataSource__Provider=postgis");

    [UnitTheory]
    [InlineData("duckdb", "Development")]
    [InlineData("mysql", "Test")]
    [InlineData(null, "Production")]
    [InlineData("postgis", "Production")]
    [InlineData("postgresql", "Production")]
    public void ProductionRefusal_PostgreSqlPrimaryOrDevelopmentOrTest_IsNull(string? provider, string environmentName)
        => PrimaryCatalogStore.ProductionRefusal(Build(provider), HostEnvironment(environmentName)).Should().BeNull();

    private static IHostEnvironment HostEnvironment(string name)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(name);
        return environment;
    }

    private static IConfiguration Build(string? provider)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataSource:Provider"] = provider })
            .Build();
}
