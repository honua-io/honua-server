// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Security;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.Security;

public sealed class StoreProfileWritePolicyTests
{
    private const string PersistenceReached = "persistence reached";
    private static bool AdditionalStoresExpected =>
        Environment.GetEnvironmentVariable("HONUA_EXPECTED_STORE_PROFILE") != "postgis";

    [UnitTest]
    public void CompiledAvailability_MatchesRequestedProfile()
    {
        DataStoreProviderPolicy.AdditionalStoresIncluded.Should().Be(AdditionalStoresExpected);
    }

    [UnitTheory]
    [InlineData(DataProviderNames.MySql)]
    [InlineData(DataProviderNames.SqlServer)]
    [InlineData(DataProviderNames.GeoServicesRest)]
    public async Task CreateConnectionAsync_AdditionalStore_RefusesBeforePersistenceWhenExcluded(string provider)
    {
        await AssertRegistryWriteAsync(registry => registry.CreateConnectionAsync(Connection(provider)));
    }

    [UnitTheory]
    [InlineData(DataProviderNames.MySql)]
    [InlineData(DataProviderNames.SqlServer)]
    [InlineData(DataProviderNames.GeoServicesRest)]
    public async Task UpdateConnectionAsync_AdditionalStore_RefusesBeforePersistenceWhenExcluded(string provider)
    {
        await AssertRegistryWriteAsync(registry => registry.UpdateConnectionAsync(Connection(provider)));
    }

    private static async Task AssertRegistryWriteAsync(Func<PostgresSecureConnectionRegistry, Task> write)
    {
        var primary = new RejectingPrimaryConnectionProvider();
        using var pools = new SecureConnectionDataSourceCache(new ConfigurationBuilder().Build());
        var registry = new PostgresSecureConnectionRegistry(primary,
            NullLogger<PostgresSecureConnectionRegistry>.Instance, pools);
        Func<Task> save = () => write(registry);

        var expected = AdditionalStoresExpected ? PersistenceReached : "*-full*";
        await save.Should().ThrowAsync<InvalidOperationException>().WithMessage(expected);
    }

    private static DataConnection Connection(string provider) => new()
    {
        Name = "fixture",
        Host = "localhost",
        Port = 5432,
        DatabaseName = "fixture",
        Username = "fixture",
        Provider = provider
    };

    private sealed class RejectingPrimaryConnectionProvider : IPrimaryDatabaseConnectionProvider
    {
        public string GetConnectionString() => throw new InvalidOperationException(PersistenceReached);

        public Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<DbConnection>(new InvalidOperationException(PersistenceReached));

        public Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation,
            CancellationToken cancellationToken = default) => operation();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation,
            CancellationToken cancellationToken = default) => operation();
    }
}
