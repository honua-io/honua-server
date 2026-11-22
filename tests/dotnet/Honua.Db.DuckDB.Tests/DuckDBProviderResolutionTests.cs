// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Warehouse.Abstractions;
using Honua.Core.Features.Warehouse.Domain;
using Honua.Core.Queries.Filters;
using Honua.Db.DuckDB.Queries.Filters;
using Honua.Db.DuckDB.Features.HealthCheck;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.DuckDB.Tests;

public sealed class DuckDBProviderResolutionTests
{
    [Fact]
    public void AddDuckDBServices_RegistersDatabaseHealthChecker()
    {
        // ReadinessCheckService takes IDatabaseHealthChecker as a REQUIRED ctor param.

        using var provider = BuildDuckDbServiceProvider();
        using var scope = provider.CreateScope();

        var checker = scope.ServiceProvider.GetRequiredService<IDatabaseHealthChecker>();

        Assert.IsType<DuckDBDatabaseHealthChecker>(checker);
    }

    [Fact]
    public void AddDuckDBServices_RegistersDatabaseHealthChecker_AsScoped()
    {
        // The checker is scoped to align with the scoped IDatabaseConnectionProvider it depends on.
        using var provider = BuildDuckDbServiceProvider();

        // Resolve from two separate scopes to confirm they are different instances.
        DuckDBDatabaseHealthChecker? first;
        DuckDBDatabaseHealthChecker? second;

        using (var s1 = provider.CreateScope())
        {
            first = s1.ServiceProvider.GetRequiredService<IDatabaseHealthChecker>() as DuckDBDatabaseHealthChecker;
        }
        using (var s2 = provider.CreateScope())
        {
            second = s2.ServiceProvider.GetRequiredService<IDatabaseHealthChecker>() as DuckDBDatabaseHealthChecker;
        }

        Assert.NotNull(first);
        Assert.NotNull(second);
        // Scoped instances across different scopes must be different objects.
        Assert.NotSame(first, second);
    }

    [Fact]
    public void AddDuckDBServices_RegistersSqlFilterTranslator()
    {
        using var provider = BuildDuckDbServiceProvider();
        using var scope = provider.CreateScope();

        var translator = scope.ServiceProvider.GetRequiredService<ISqlFilterTranslator>();

        Assert.IsType<DuckDbSqlFilterTranslator>(translator);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("true", true)]
    public void AddDuckDBServices_AdvertisesH3OnlyWhenEnabled(string? setting, bool enabled)
    {
        using var provider = BuildDuckDbServiceProvider(setting);
        var descriptor = provider.GetRequiredService<IWarehouseProviderCapabilityDescriptor>();

        var capabilities = descriptor.Describe(new WarehouseSourceCapabilityRequest
        {
            ProviderName = descriptor.ProviderName,
            SourceId = "test",
        });

        Assert.Equal(enabled, capabilities.Aggregation.SupportsH3Aggregation);
        Assert.Equal(enabled, capabilities.Aggregation.SupportsH3Polyfill);
        Assert.Equal(enabled, capabilities.Aggregation.SupportsH3KRing);
        Assert.Equal(enabled, capabilities.Aggregation.RequiredExtensions.Contains("h3"));
    }

    private static ServiceProvider BuildDuckDbServiceProvider(string? enableH3 = null)
    {
        // Minimal DuckDB configuration — in-memory mode with no layers so no real
        // file or schema discovery is attempted. The DI tests resolve types only;
        // they do not execute SQL or open connections.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DuckDB:DatabasePath"] = ":memory:",
                ["DuckDB:ReadOnly"] = "false",
                ["DuckDB:EnableH3"] = enableH3,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        ServiceCollectionExtensions.AddDuckDBServices(services, config);
        return services.BuildServiceProvider();
    }
}
