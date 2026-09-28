// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Infrastructure.Caching;
using Honua.Db.Postgres.Features.Security;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using Npgsql;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[Collection("Database")]
public sealed class PostgresBoundConnectionProviderTests(PostgresFixture fixture) : IAsyncLifetime
{
    private string _sourceString = null!;

    public async Task InitializeAsync()
    {
        _sourceString = await fixture.CreateIsolatedDatabaseAsync(nameof(PostgresBoundConnectionProviderTests));
        await using var connection = new NpgsqlConnection(_sourceString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            CREATE TABLE public.pool_probe (id bigint PRIMARY KEY, name text);
            INSERT INTO public.pool_probe VALUES (7, 'source database');
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => fixture.DropDatabaseAsync(new NpgsqlConnectionStringBuilder(_sourceString).Database!);

    [IntegrationTest]
    public async Task BoundReader_EnforcesSharedLimit_AndReleasesEarlyDisposedStream()
    {
        using var harness = new Harness(fixture.DataSource);
        var reader = CreateReader(harness);
        await using var stream = reader.StreamFeaturesAsync(1, new FeatureQuery { Limit = 1 }).GetAsyncEnumerator();
        (await stream.MoveNextAsync()).Should().BeTrue();
        stream.Current.Attributes["name"].Should().Be("source database");
        harness.Gate.AvailableSlots.Should().Be(0);
        harness.Tracker.GetActiveCount().Should().Be(1);

        var blocked = () => reader.QueryPageAsync(1, new FeatureQuery { Limit = 1 });
        await blocked.Should().ThrowAsync<ServiceUnavailableException>();
        harness.Metrics.GetTotalTimeouts().Should().Be(1);
        harness.Gate.AvailableSlots.Should().Be(0);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledRead = () => reader.QueryPageAsync(1, new FeatureQuery { Limit = 1 }, cancelled.Token);
        await cancelledRead.Should().ThrowAsync<OperationCanceledException>();
        harness.Gate.AvailableSlots.Should().Be(0);

        await stream.DisposeAsync();
        harness.Gate.AvailableSlots.Should().Be(1);
        harness.Tracker.GetActiveCount().Should().Be(0);
        var result = await reader.QueryPageAsync(1, new FeatureQuery { Limit = 1 });
        result.Items.Should().ContainSingle().Which.Id.Should().Be(7);
        harness.Gate.AvailableSlots.Should().Be(1);
    }

    [IntegrationTest]
    public async Task BoundPool_UsesConfiguredLimitsAndSessionSettings_InTheSourceDatabase()
    {
        using var harness = new Harness(fixture.DataSource);
        await using var connection = await harness.Bound.OpenConnectionAsync("source", _sourceString);
        var settings = new NpgsqlConnectionStringBuilder(connection.Connection.ConnectionString);
        settings.MaxPoolSize.Should().Be(1);
        settings.MinPoolSize.Should().Be(0);
        settings.CommandTimeout.Should().Be(17);
        await using var command = new NpgsqlCommand("SELECT current_database(), current_setting('statement_timeout')", connection);
        await using var result = await command.ExecuteReaderAsync();
        (await result.ReadAsync()).Should().BeTrue();
        result.GetString(0).Should().Be(new NpgsqlConnectionStringBuilder(_sourceString).Database);
        result.GetString(1).Should().Be("19s");
        harness.Metrics.GetPoolUtilization().Should().Be(1);
    }

    [IntegrationTest]
    public async Task EncryptedBinding_ResolvesBeforeAdmission_AndDoesNotDoubleAcquire()
    {
        using var harness = new Harness(fixture.DataSource);
        var encryption = Substitute.For<IConnectionEncryptionService>();
        encryption.DecryptConnectionStringAsync(Arg.Any<byte[]>(), Arg.Any<int>())
            .Returns(_ => ResolveUsingMetadataAsync());
        var binding = new DataConnection
        {
            Id = "encrypted-source", IsEncrypted = true, EncryptedConnectionString = [1], EncryptionKeyVersion = 1
        };
        var reader = CreateReader(harness, binding, encryption);
        (await reader.QueryPageAsync(1, new FeatureQuery { Limit = 1 })).Items.Should().ContainSingle();
        (await reader.QueryPageAsync(1, new FeatureQuery { Limit = 1 })).Items.Should().ContainSingle();
        await encryption.Received(1).DecryptConnectionStringAsync(Arg.Any<byte[]>(), Arg.Any<int>());
        harness.Gate.AvailableSlots.Should().Be(1);
        harness.Tracker.GetActiveCount().Should().Be(0);

        async Task<string> ResolveUsingMetadataAsync()
        {
            harness.Gate.AvailableSlots.Should().Be(1);
            await using var metadata = await harness.Primary.OpenConnectionAsync();
            harness.Gate.AvailableSlots.Should().Be(0);
            return _sourceString;
        }
    }

    [IntegrationTest]
    public async Task OpenFailure_ReleasesAdmission_AndRecordsFailureWithoutSuccessfulLeaseDuration()
    {
        using var harness = new Harness(fixture.DataSource);
        var invalid = new NpgsqlConnectionStringBuilder(_sourceString) { Username = "missing_pool_probe_user" }.ConnectionString;
        var open = () => harness.Bound.OpenConnectionAsync("source", invalid);
        await open.Should().ThrowAsync<PostgresException>();
        harness.Gate.AvailableSlots.Should().Be(1);
        harness.Tracker.GetActiveCount().Should().Be(0);
        harness.Metrics.GetTotalFailures().Should().Be(1);
        harness.Gate.GetSnapshot().DurationEwmaMs.Should().Be(0);
        await using var recovered = await harness.Bound.OpenConnectionAsync("source", _sourceString);
        harness.Gate.AvailableSlots.Should().Be(0);
    }

    [IntegrationTest]
    public async Task CancellationWhileWaitingForPhysicalPool_ReleasesAdmissionWithoutRecordingFailure()
    {
        using var harness = new Harness(fixture.DataSource);
        var dataSource = harness.Cache.GetOrCreate("bound-id:source", _sourceString);
        await using var occupied = await dataSource.OpenConnectionAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = harness.Bound.OpenConnectionAsync("source", _sourceString, cancellation.Token);
        harness.Gate.AvailableSlots.Should().Be(0);
        cancellation.Cancel();
        var cancelledOpen = async () => await pending;
        await cancelledOpen.Should().ThrowAsync<OperationCanceledException>();
        harness.Gate.AvailableSlots.Should().Be(1);
        harness.Metrics.GetTotalFailures().Should().Be(0);
        harness.Tracker.GetActiveCount().Should().Be(0);
        harness.Gate.GetSnapshot().DurationEwmaMs.Should().Be(0);
    }

    [IntegrationTest]
    public async Task RotationWhileQueued_ResolvesPoolAfterAdmission()
    {
        using var harness = new Harness(fixture.DataSource);
        harness.Cache.GetOrCreate("bound-id:source", _sourceString);
        await using var blocker = await harness.Primary.OpenConnectionAsync();
        var pending = harness.Bound.OpenConnectionAsync("source", _sourceString);
        harness.Gate.GetSnapshot().QueuedWaiters.Should().Be(1);
        harness.Cache.GetOrCreate("bound-id:source",
            new NpgsqlConnectionStringBuilder(_sourceString) { ApplicationName = "rotated" }.ConnectionString);
        await blocker.DisposeAsync();
        await using var lease = await pending;
        lease.Connection.Database.Should().Be(new NpgsqlConnectionStringBuilder(_sourceString).Database);
        harness.Gate.AvailableSlots.Should().Be(0);
        harness.Metrics.GetTotalFailures().Should().Be(0);
        await lease.DisposeAsync();
        harness.Gate.AvailableSlots.Should().Be(1);
    }

    [IntegrationTest]
    public async Task RotationBetweenResolutionAndOpen_RetriesOnceWithinSameAdmissionSlot()
    {
        using var harness = new Harness(fixture.DataSource);
        using var retired = NpgsqlDataSource.Create(_sourceString);
        retired.Dispose();
        var live = harness.Cache.GetOrCreate("bound-id:source", _sourceString);
        var resolutions = 0;
        await using var connection = await harness.Primary.OpenConnectionAsync(() =>
        {
            harness.Gate.AvailableSlots.Should().Be(0);
            return ++resolutions == 1 ? retired : live;
        });
        resolutions.Should().Be(2);
        harness.Metrics.GetTotalFailures().Should().Be(0);
        harness.Tracker.GetActiveCount().Should().Be(1);
        await connection.DisposeAsync();
        harness.Gate.AvailableSlots.Should().Be(1);
    }

    private PostgresStorageMappedFeatureReader CreateReader(
        Harness harness, DataConnection? binding = null, IConnectionEncryptionService? encryption = null)
        => new(harness.Primary,
            new DefaultObjectPoolProvider().Create(new Honua.Core.Features.Infrastructure.ServiceRegistration.DictionaryPooledObjectPolicy()),
            new MetadataV2Resource
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "pool-probe", Name = "pool-probe" },
                SchemaFields = [new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }]
            },
            new FeatureStorageMapping("pool_probe", SchemaName: "public", PrimaryKeyColumn: "id"),
            binding ?? new DataConnection { Id = "source", IsEncrypted = false, ConnectionString = _sourceString },
            encryption, boundConnectionProvider: harness.Bound);

    private sealed class Harness : IDisposable
    {
        public readonly QueryConcurrencyGate Gate;
        public readonly Tracker Tracker = new();
        public readonly ConnectionPoolMetrics Metrics;
        public readonly SecureConnectionDataSourceCache Cache;
        public readonly CachingDatabaseConnectionProvider Primary;
        public readonly PostgresBoundConnectionProvider Bound;

        public Harness(NpgsqlDataSource defaultSource)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Limits:Connections:MaxConnectionPoolSize"] = "1",
                ["Limits:Connections:MinConnectionPoolSize"] = "0",
                ["Limits:Connections:MaxConcurrentQueries"] = "1",
                ["Limits:Connections:ConnectionAcquisitionTimeoutSeconds"] = "1",
                ["Limits:Connections:CommandTimeoutSeconds"] = "17",
                ["Limits:Connections:StatementTimeout"] = "00:00:19"
            }).Build();
            Gate = new QueryConcurrencyGate(PostgresDataSourceFactory.ResolveConnectionLimits(configuration));
            Metrics = new ConnectionPoolMetrics(Tracker);
            Cache = new SecureConnectionDataSourceCache(configuration);
            Primary = new CachingDatabaseConnectionProvider(defaultSource,
                NullLogger<CachingDatabaseConnectionProvider>.Instance,
                activeDbConnectionTracker: Tracker, concurrencyGate: Gate, connectionPoolMetrics: Metrics);
            Bound = new PostgresBoundConnectionProvider(Cache, Primary);
        }

        public void Dispose()
        {
            Primary.Dispose();
            Cache.Dispose();
            Metrics.Dispose();
            Gate.Dispose();
        }
    }

    private sealed class Tracker : IActiveDbConnectionTracker
    {
        private int _active;
        public void Increment() => Interlocked.Increment(ref _active);
        public void Decrement() => Interlocked.Decrement(ref _active);
        public int GetActiveCount() => Volatile.Read(ref _active);
    }
}
