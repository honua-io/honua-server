// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Server.Features.Geoprocessing.ProcessRelease;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Server.Tests.Features.Geoprocessing;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
[Collection("Database.CoreFeatureStore")]
public sealed class PostgresGpProcessReleaseStoreLeaseTests(DatabaseFixtureAdapter fixture)
{
    [IntegrationTest]
    public async Task StoreOperations_DisposeWrappedConnectionLease_ReleasingConcurrencySlot()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresGpProcessReleaseStoreLeaseTests));
        try
        {
            await ApplyMigrationAsync(schema);
            var provider = new LeaseTrackingConnectionProvider(fixture.DataSource);
            var services = new ServiceCollection();
            services.AddSingleton<IAdoNetDatabaseConnectionProvider>(provider);
            await using var serviceProvider = services.BuildServiceProvider();

            var store = new PostgresGpProcessReleaseStore(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                TimeProvider.System,
                schema);

            _ = await store.GetReleaseAsync("sha256:missing");
            _ = await store.ListReleasesAsync(null);
            _ = await store.GetBindingAsync("missing-process");
            _ = await store.ListBindingsAsync();

            provider.ActiveLeases.Should().Be(0, "every operation must dispose its NpgsqlConnectionLease");
            provider.ReleasedLeases.Should().Be(4, "each of the 4 operations must invoke the wrapper release callback");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task ApplyMigrationAsync(string schema)
    {
        var assembly = typeof(Program).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("131_CreateGpProcessReleases.sql", StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        var sql = (await reader.ReadToEndAsync()).Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal);
        await fixture.ExecuteAsync(sql);
    }

    private sealed class LeaseTrackingConnectionProvider(NpgsqlDataSource dataSource) : IAdoNetDatabaseConnectionProvider
    {
        private int _activeLeases;
        private int _releasedLeases;
        public int ActiveLeases => Volatile.Read(ref _activeLeases);
        public int ReleasedLeases => Volatile.Read(ref _releasedLeases);
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            Interlocked.Increment(ref _activeLeases);
            return new SemaphoreReleasingConnection(connection, () =>
            {
                connection.State.Should().Be(ConnectionState.Closed);
                Interlocked.Decrement(ref _activeLeases);
                Interlocked.Increment(ref _releasedLeases);
            });
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            return (connection, await connection.BeginTransactionAsync(isolationLevel, cancellationToken));
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default) => operation();
        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default) => operation();
    }
}
