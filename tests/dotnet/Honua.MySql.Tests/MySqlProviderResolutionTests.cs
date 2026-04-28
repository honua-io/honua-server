// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.MySql.Features.FeatureStore;
using Honua.MySql.Features.FeatureStore.Services;
using Honua.MySql.Features.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.MySql.Tests;

/// <summary>
/// Verifies that the canonical "mysql" name and the "mariadb" alias both resolve to
/// the MySQL/MariaDB feature provider through <see cref="IFeatureDataProviderRegistry"/>.
/// </summary>
public class MySqlProviderResolutionTests
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
