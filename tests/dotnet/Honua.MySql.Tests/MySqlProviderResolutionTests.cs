// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.MySql.Features.FeatureStore;
using Honua.MySql.Features.FeatureStore.Services;
using Honua.MySql.Features.HealthCheck;
using Honua.MySql.Features.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.MySql.Tests;

/// <summary>
/// Verifies that the canonical "mysql" name and the "mariadb" alias both resolve to
/// the MySQL/MariaDB feature provider through <see cref="IFeatureDataProviderRegistry"/>.
/// </summary>
public sealed class MySqlProviderResolutionTests
{
    [Fact]
    public void DataProviderNames_MariadbAlias_NormalisesToMysql()
    {
        Assert.Equal("mysql", DataProviderNames.Normalize("mariadb"));
        Assert.Equal("mysql", DataProviderNames.Normalize("MariaDB"));
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("MYSQL")]
    [InlineData("MariaDB")]
    [InlineData("mariadb")]
    public void Registry_ResolvesMySqlOrMariadb_ToMySqlFeatureStore(string providerNameInput)
    {
        var provider = CreateMySqlFeatureProvider();
        var registry = new FeatureDataProviderRegistry([provider]);

        Assert.True(registry.TryGetProvider(providerNameInput, out var resolved));
        Assert.Same(provider, resolved);
    }

    [Fact]
    public void MySqlFeatureStore_AdvertisesReadOnlyMySqlCapabilities()
    {
        var provider = CreateMySqlFeatureProvider();

        Assert.Same(FeatureProviderCapabilities.ReadOnlyMySql, provider.Capabilities);
        Assert.True(provider.Capabilities.SupportsQuery);
        Assert.True(provider.Capabilities.SupportsCount);
        Assert.True(provider.Capabilities.SupportsExtent);
        Assert.False(provider.Capabilities.SupportsStatistics);
        Assert.False(provider.Capabilities.Outputs.SupportsNativeMvt);
        Assert.False(provider.Capabilities.Outputs.SupportsNativeFlatGeobuf);
        Assert.False(provider.Capabilities.Outputs.SupportsNativeGml);
        Assert.False(provider.Capabilities.Outputs.SupportsNativeGeobuf);
        Assert.False(provider.Capabilities.Outputs.SupportsStreamingGeoJson);
        Assert.False(provider.Capabilities.Edits.SupportsCreate);
        Assert.False(provider.Capabilities.Edits.SupportsUpdate);
        Assert.False(provider.Capabilities.Edits.SupportsDelete);
        Assert.Null(provider.Writer);
    }

    [Fact]
    public void MySqlFeatureStore_ProviderName_IsMySqlCanonical()
    {
        var provider = CreateMySqlFeatureProvider();

        Assert.Equal(DataProviderNames.MySql, provider.ProviderName);
    }

    [Theory]
    [InlineData(typeof(IFeatureDataProvider))]
    [InlineData(typeof(IFeatureReader))]
    [InlineData(typeof(IPagedFeatureReader))]
    [InlineData(typeof(IStreamingFeatureStore))]
    [InlineData(typeof(IFeatureWriter))]
    [InlineData(typeof(IReplicaRepository))]
    [InlineData(typeof(IChangeTracker))]
    [InlineData(typeof(ITileProvider))]
    [InlineData(typeof(IGmlFeatureStore))]
    [InlineData(typeof(IDatabaseHealthChecker))]
    public void AddMySqlServices_RegistersProtocolFallbacks_ForAdvertisedProtocolHandlers(Type serviceType)
    {
        // The MySQL slice advertises EnabledProtocols including FeatureServer, OgcFeatures,
        // and Grpc. Those protocol handlers require IFeatureWriter and IStreamingFeatureStore
        // (constructor injected), and the WFS 2.0 path requires IGmlFeatureStore. The
        // replica/change-tracking paths require their own segregated services. This test
        // pins the read/query-only fallback contract so DI activation does not regress.
        using var provider = BuildMySqlServiceProvider();
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetService(serviceType);

        Assert.NotNull(resolved);
    }

    [Fact]
    public async Task AddMySqlServices_ReadOnlyFeatureWriter_RejectsCreate()
    {
        using var provider = BuildMySqlServiceProvider();
        using var scope = provider.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IFeatureWriter>();

        Assert.IsType<ReadOnlyFeatureWriter>(writer);
        var feature = Feature.Create(0, null);
        await Assert.ThrowsAsync<NotSupportedException>(() => writer.CreateAsync(1, feature));
    }

    [Fact]
    public async Task AddMySqlServices_ReadOnlyChangeTracker_ReportsZeroGeneration()
    {
        using var provider = BuildMySqlServiceProvider();
        using var scope = provider.CreateScope();
        var tracker = scope.ServiceProvider.GetRequiredService<IChangeTracker>();

        Assert.IsType<NoOpChangeTracker>(tracker);
        Assert.Equal(0L, await tracker.GetCurrentGenerationAsync());
        var changes = await tracker.GetChangesSinceAsync(0L, [1]);
        Assert.Empty(changes);
    }

    [Fact]
    public async Task AddMySqlServices_ReadOnlyReplicaRepository_ReturnsEmpty()
    {
        using var provider = BuildMySqlServiceProvider();
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IReplicaRepository>();

        Assert.IsType<NoOpReplicaRepository>(repo);
        Assert.Null(await repo.GetAsync("anything"));
        var listed = await repo.ListByServiceAsync("anything");
        Assert.Empty(listed);
    }

    [Fact]
    public void AddMySqlServices_RegistersMySqlDatabaseHealthChecker()
    {
        // ReadinessCheckService requires IDatabaseHealthChecker; without this registration
        // the /ready endpoint would fail DI activation under DataSource:Provider=mysql.
        using var provider = BuildMySqlServiceProvider();
        using var scope = provider.CreateScope();

        var checker = scope.ServiceProvider.GetRequiredService<IDatabaseHealthChecker>();

        Assert.IsType<MySqlDatabaseHealthChecker>(checker);
    }

    private static ServiceProvider BuildMySqlServiceProvider()
    {
        // Minimal provider configuration — connection string is parsed at DataSource build
        // time, but no connection is opened until a query runs. The registration tests do
        // not execute SQL; they only verify DI activation succeeds for the contract.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataSource:Provider"] = "mysql",
                ["MySql:ConnectionString"] = "Server=localhost;Database=test;User=test;Password=test;",
                ["MySql:Layers:0:Id"] = "1",
                ["MySql:Layers:0:Name"] = "test_layer",
                ["MySql:Layers:0:Table"] = "test",
                ["MySql:Layers:0:GeometryColumn"] = "geom",
                ["MySql:Layers:0:PrimaryKeyColumn"] = "id",
                ["MySql:Layers:0:Srid"] = "4326",
                ["MySql:Layers:0:GeometryType"] = "Point",
                ["MySql:Layers:0:Attributes:0"] = "name"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        ServiceCollectionExtensions.AddMySqlServices(services, config);
        return services.BuildServiceProvider();
    }

    private static MySqlFeatureStore CreateMySqlFeatureProvider()
    {
        var registry = new MySqlLayerMappingRegistry([]);
        var queryBuilder = new MySqlFeatureQueryBuilder(registry);
        var dataAccess = new MySqlFeatureDataAccess(
            new ThrowingConnectionProvider(),
            registry,
            performanceMonitor: null,
            NullLogger<MySqlFeatureDataAccess>.Instance);
        return new MySqlFeatureStore(queryBuilder, dataAccess);
    }

    private sealed class ThrowingConnectionProvider : Honua.Core.Features.Infrastructure.Abstractions.IDatabaseConnectionProvider
    {
        public string GetConnectionString() => string.Empty;

        public Task<System.Data.Common.DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Connection provider not used in resolution tests.");

        public Task<(System.Data.Common.DbConnection Connection, System.Data.Common.DbTransaction Transaction)> OpenTransactionAsync(
            System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Connection provider not used in resolution tests.");

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => operation();
    }
}
