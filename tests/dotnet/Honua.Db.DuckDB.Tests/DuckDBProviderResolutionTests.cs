// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.DuckDB.Queries.Filters;
using Honua.DuckDB.Features.HealthCheck;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.DuckDB.Tests;

/// <summary>
/// Verifies that DI registration for the DuckDB provider activates all required
/// protocol-handler contracts, including the readiness-check health checker (PA-158).
/// </summary>
public sealed class DuckDBProviderResolutionTests
{
    [Fact]
    public void AddDuckDBServices_RegistersDatabaseHealthChecker()
    {
        // ReadinessCheckService takes IDatabaseHealthChecker as a REQUIRED ctor param.
        // Before PA-158, resolving /healthz/ready under DataSource:Provider=duckdb threw
        // InvalidOperationException because no IDatabaseHealthChecker was registered.
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

    private static ServiceProvider BuildDuckDbServiceProvider()
    {
        // Minimal DuckDB configuration — in-memory mode with no layers so no real
        // file or schema discovery is attempted. The DI tests resolve types only;
        // they do not execute SQL or open connections.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DuckDB:DatabasePath"] = ":memory:",
                ["DuckDB:ReadOnly"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        ServiceCollectionExtensions.AddDuckDBServices(services, config);
        return services.BuildServiceProvider();
    }
}
