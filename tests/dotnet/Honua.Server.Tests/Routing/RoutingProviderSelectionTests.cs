// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Honua.Server.Tests.Routing;

/// <summary>
/// Unit tests (no database) for routing provider selection in
/// <see cref="ServiceCollectionExtensions.AddRouting"/>. Verifies that the
/// default does NOT register the pgRouting provider on non-Postgres data
/// providers, so the NAServer adapter can respond clearly instead of executing
/// pgRouting SQL against an incompatible database.
/// </summary>
public sealed class RoutingProviderSelectionTests
{
    private static Type? SelectedProviderType(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s =>
                new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddRouting(configuration);

        return services
            .LastOrDefault(d => d.ServiceType == typeof(IRoutingProvider))
            ?.ImplementationType;
    }

    [Fact]
    public void AddRouting_DefaultWithPostgresDataProvider_RegistersPgRouting()
    {
        Assert.Equal(typeof(PgRoutingProvider), SelectedProviderType(("DataSource:Provider", "postgres")));
    }

    [Fact]
    public void AddRouting_DefaultWithUnsetDataProvider_RegistersPgRouting()
    {
        // Postgres is the default data provider, so routing defaults to pgRouting.
        Assert.Equal(typeof(PgRoutingProvider), SelectedProviderType());
    }

    [Theory]
    [InlineData("duckdb")]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    [InlineData("sqlserver")]
    public void AddRouting_DefaultWithNonPostgresDataProvider_RegistersUnavailable(string dataProvider)
    {
        Assert.Equal(
            typeof(UnavailableRoutingProvider),
            SelectedProviderType(("DataSource:Provider", dataProvider)));
    }

    [Fact]
    public void AddRouting_ExplicitPgRoutingOnNonPostgres_HonorsOptIn()
    {
        Assert.Equal(
            typeof(PgRoutingProvider),
            SelectedProviderType(
                ("Routing:Provider", "pgrouting"),
                ("DataSource:Provider", "duckdb")));
    }

    [Fact]
    public void AddRouting_ExplicitMock_RegistersMock()
    {
        Assert.Equal(typeof(MockRoutingProvider), SelectedProviderType(("Routing:Provider", "mock")));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("unavailable")]
    public void AddRouting_ExplicitNone_RegistersUnavailable(string providerName)
    {
        Assert.Equal(
            typeof(UnavailableRoutingProvider),
            SelectedProviderType(("Routing:Provider", providerName)));
    }
}
