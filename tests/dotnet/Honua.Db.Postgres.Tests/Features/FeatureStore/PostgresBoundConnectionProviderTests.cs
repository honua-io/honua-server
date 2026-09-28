// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
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

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundReader_SmallintBatch_ReleasesAdmissionAfterDisposalFallbackAndCancellation(bool multiplexing)
    {
        await using var setup = new NpgsqlConnection(_sourceString);
        await setup.OpenAsync();
        await using (var command = new NpgsqlCommand("ALTER TABLE public.pool_probe ALTER COLUMN name TYPE smallint USING 1;", setup))
        {
            await command.ExecuteNonQueryAsync();
        }
        using var harness = new Harness(fixture.DataSource, multiplexing: multiplexing);
        var reader = CreateReader(harness, smallintHint: true);
        var query = new FeatureQuery
        {
            Limit = 10,
            SqlFilter = new SqlFragment("NULLIF(\"attributes\" ->> 'name', '')::integer = @p0", [1L])
        };
        await using (var stream = reader.StreamFeaturesAsync(1, query).GetAsyncEnumerator())
        {
            (await stream.MoveNextAsync()).Should().BeTrue();
            harness.Gate.AvailableSlots.Should().Be(0);
            harness.Tracker.GetActiveCount().Should().Be(1);
        }
        harness.Gate.AvailableSlots.Should().Be(1);
        harness.Tracker.GetActiveCount().Should().Be(0);

        // A parse failure in the discarded statement must not acquire a second lease.
        await using (var command = new NpgsqlCommand("ALTER TABLE public.pool_probe ALTER COLUMN name TYPE boolean USING name = 1;", setup))
        {
            await command.ExecuteNonQueryAsync();
        }
        (await reader.QueryPageAsync(1, query)).Items.Should().ContainSingle().Which.Id.Should().Be(7);
        harness.Gate.AvailableSlots.Should().Be(1);
        harness.Tracker.GetActiveCount().Should().Be(0);
        harness.Metrics.GetTotalTimeouts().Should().Be(0);

        // Cancel while the first, row-free verification is waiting for the relation lock.
        await using var transaction = await setup.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("LOCK TABLE public.pool_probe IN ACCESS EXCLUSIVE MODE", setup, transaction))
        {
            await command.ExecuteNonQueryAsync();
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var cancelledRead = () => reader.QueryPageAsync(1, query, cancellation.Token);
        await cancelledRead.Should().ThrowAsync<OperationCanceledException>();
        harness.Gate.AvailableSlots.Should().Be(1);
        harness.Tracker.GetActiveCount().Should().Be(0);
        await transaction.RollbackAsync();
        (await reader.QueryPageAsync(1, query)).Items.Should().ContainSingle();
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

    [IntegrationTheory]
    [InlineData("primary_schema", null, false)]
    [InlineData(null, "request_schema", false)]
    [InlineData("primary_schema", "request_schema", false)]
    [InlineData("primary_schema", "request_schema", true)]
    public async Task BoundReader_WithoutSchema_PreservesSourceSearchPath(
        string? primarySchema, string? requestSchema, bool explicitSourceSchema)
    {
        await using (var setup = new NpgsqlConnection(_sourceString))
        {
            await setup.OpenAsync();
            var roleSchema = SchemaSearchPath.ValidateAndQuote(new NpgsqlConnectionStringBuilder(_sourceString).Username!);
            await using var command = new NpgsqlCommand($"""
                CREATE SCHEMA {roleSchema};
                CREATE TABLE {roleSchema}.schema_probe (id bigint PRIMARY KEY, name text);
                INSERT INTO {roleSchema}.schema_probe VALUES (7, 'role schema');
                CREATE SCHEMA source_explicit;
                CREATE TABLE source_explicit.schema_probe (id bigint PRIMARY KEY, name text);
                INSERT INTO source_explicit.schema_probe VALUES (17, 'explicit schema');
                """, setup);
            await command.ExecuteNonQueryAsync();
        }

        using var harness = new Harness(fixture.DataSource, primarySchema, requestSchema);
        var sourceString = explicitSourceSchema
            ? new NpgsqlConnectionStringBuilder(_sourceString) { SearchPath = "source_explicit" }.ConnectionString
            : _sourceString;
        var reader = CreateReader(harness,
            new DataConnection { Id = "source", IsEncrypted = false, ConnectionString = sourceString },
            schemaName: null, tableName: "schema_probe");

        // Reuse the physical pool as well as checking its initial session.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await reader.QueryPageAsync(1, new FeatureQuery { Limit = 1 });
            result.Items.Should().ContainSingle().Which.Id.Should().Be(explicitSourceSchema ? 17 : 7);
            harness.Gate.AvailableSlots.Should().Be(1);
        }
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
            Id = "encrypted-source",
            IsEncrypted = true,
            EncryptedConnectionString = [1],
            EncryptionKeyVersion = 1
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

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundPool_PreservesSourceStartupOptions_AndAppliesConfiguredTimeouts(bool multiplexing)
    {
        await using (var setup = new NpgsqlConnection(_sourceString))
        {
            await setup.OpenAsync();
            await using var create = new NpgsqlCommand("CREATE SCHEMA source_options", setup);
            await create.ExecuteNonQueryAsync();
        }

        using var harness = new Harness(fixture.DataSource, "primary_schema", "request_schema", multiplexing);
        var sourceString = new NpgsqlConnectionStringBuilder(_sourceString)
        {
            Options = "-c search_path=source_options -c statement_timeout=30s"
        }.ConnectionString;
        await using var connection = await harness.Bound.OpenConnectionAsync("source", sourceString);
        await using var command = new NpgsqlCommand("SELECT current_schema(), current_setting('statement_timeout')", connection);
        await using var result = await command.ExecuteReaderAsync();
        (await result.ReadAsync()).Should().BeTrue();
        result.GetString(0).Should().Be("source_options");
        result.GetString(1).Should().Be("19s");
    }

    [IntegrationTest]
    public async Task OpenFailure_ReleasesAdmission_AndRecordsFailureWithoutSuccessfulLeaseDuration()
    {
        using var harness = new Harness(fixture.DataSource);
        var invalid = new NpgsqlConnectionStringBuilder(_sourceString) { Username = "missing_pool_probe_user" }.ConnectionString;
        var failedAcquisition = harness.Cache.Acquire("bound-id:source", invalid, preservePrimarySchema: false);
        var failedSource = failedAcquisition.DataSource;
        failedAcquisition.Dispose();
        var open = () => harness.Bound.OpenConnectionAsync("source", invalid);
        await open.Should().ThrowAsync<PostgresException>();
        harness.Gate.AvailableSlots.Should().Be(1);
        harness.Tracker.GetActiveCount().Should().Be(0);
        harness.Metrics.GetTotalFailures().Should().Be(1);
        harness.Gate.GetSnapshot().DurationEwmaMs.Should().Be(0);
        await using var recovered = await harness.Bound.OpenConnectionAsync("source", _sourceString);
        harness.Gate.AvailableSlots.Should().Be(0);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await using var unexpected = await failedSource.OpenConnectionAsync();
        });
    }

    [IntegrationTest]
    public async Task CancellationWhileWaitingForPhysicalPool_ReleasesAdmissionWithoutRecordingFailure()
    {
        using var harness = new Harness(fixture.DataSource);
        using var acquisition = harness.Cache.Acquire("bound-id:source", _sourceString, preservePrimarySchema: false);
        await using var occupied = await acquisition.DataSource.OpenConnectionAsync();
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
        acquisition.Dispose();
        using var replacement = harness.Cache.Acquire("bound-id:source",
            new NpgsqlConnectionStringBuilder(_sourceString) { ApplicationName = "after-cancellation" }.ConnectionString,
            preservePrimarySchema: false);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await using var unexpected = await acquisition.DataSource.OpenConnectionAsync();
        });
    }

    [IntegrationTest]
    public async Task RotationWhileQueued_ResolvesPoolAfterAdmission()
    {
        using var harness = new Harness(fixture.DataSource);
        using var original = harness.Cache.Acquire("bound-id:source", _sourceString, preservePrimarySchema: false);
        await using var blocker = await harness.Primary.OpenConnectionAsync();
        var pending = harness.Bound.OpenConnectionAsync("source", _sourceString);
        harness.Gate.GetSnapshot().QueuedWaiters.Should().Be(1);
        using var rotated = harness.Cache.Acquire("bound-id:source",
            new NpgsqlConnectionStringBuilder(_sourceString) { ApplicationName = "rotated" }.ConnectionString,
            preservePrimarySchema: false);
        await blocker.DisposeAsync();
        await using var lease = await pending;
        lease.Connection.Database.Should().Be(new NpgsqlConnectionStringBuilder(_sourceString).Database);
        harness.Gate.AvailableSlots.Should().Be(0);
        harness.Metrics.GetTotalFailures().Should().Be(0);
        await lease.DisposeAsync();
        harness.Gate.AvailableSlots.Should().Be(1);
    }

    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RepeatedRotationBetweenAcquisitionAndOpen_PreservesPinnedPoolAndAdmission(bool multiplexing, bool admissionEnabled)
    {
        using var harness = new Harness(fixture.DataSource, multiplexing: multiplexing, admissionEnabled: admissionEnabled);
        NpgsqlDataSource? retired = null;
        var resolutions = 0;
        await using var connection = await harness.Primary.OpenConnectionAsync(() =>
        {
            harness.Gate.AvailableSlots.Should().Be(admissionEnabled ? 0 : 1);
            resolutions++;
            var acquisition = harness.Cache.Acquire("bound-id:source", _sourceString, preservePrimarySchema: false);
            retired = acquisition.DataSource;
            // Rotate repeatedly before the provider can start opening this source.
            // Pinning must survive any number of overlapping metadata generations.
            for (var generation = 0; generation < 3; generation++)
            {
                using var replacement = harness.Cache.Acquire("bound-id:source",
                    new NpgsqlConnectionStringBuilder(_sourceString) { ApplicationName = $"rotation-{generation}" }.ConnectionString,
                    preservePrimarySchema: false);
            }

            return (acquisition.DataSource, acquisition);
        });
        resolutions.Should().Be(1);
        harness.Metrics.GetTotalFailures().Should().Be(0);
        harness.Tracker.GetActiveCount().Should().Be(1);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT 1";
            (await command.ExecuteScalarAsync()).Should().Be(1);
        }

        await connection.DisposeAsync();
        harness.Gate.AvailableSlots.Should().Be(1);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await using var unexpected = await retired!.OpenConnectionAsync();
            // A multiplexed logical open defers the disposed-pool check until binding.
            await using var transaction = await unexpected.BeginTransactionAsync();
        });
    }

    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RotationAfterOpen_KeepsReturnedConnectionUsableUntilDisposal(bool multiplexing, bool admissionEnabled)
    {
        using var harness = new Harness(fixture.DataSource, multiplexing: multiplexing, admissionEnabled: admissionEnabled);
        var initial = harness.Cache.Acquire("bound-id:source", _sourceString, preservePrimarySchema: false);
        var originalSource = initial.DataSource;
        initial.Dispose();
        await using var connection = await harness.Bound.OpenConnectionAsync("source", _sourceString);
        using var replacement = harness.Cache.Acquire("bound-id:source",
            new NpgsqlConnectionStringBuilder(_sourceString) { ApplicationName = "after-open" }.ConnectionString,
            preservePrimarySchema: false);

        // Multiplexed opens can remain unbound until a command executes, and
        // later commands still depend on the retained data source's channel.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            (await command.ExecuteScalarAsync()).Should().Be(1);
        }

        await connection.DisposeAsync();
        harness.Gate.AvailableSlots.Should().Be(1);
        harness.Tracker.GetActiveCount().Should().Be(0);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await using var unexpected = await originalSource.OpenConnectionAsync();
            // A multiplexed logical open defers the disposed-pool check until binding.
            await using var transaction = await unexpected.BeginTransactionAsync();
        });
    }

    private PostgresStorageMappedFeatureReader CreateReader(
        Harness harness, DataConnection? binding = null, IConnectionEncryptionService? encryption = null,
        string? schemaName = "public", string tableName = "pool_probe", bool smallintHint = false)
        => new(harness.Primary,
            new DefaultObjectPoolProvider().Create(new Honua.Core.Features.Infrastructure.ServiceRegistration.DictionaryPooledObjectPolicy()),
            new MetadataV2Resource
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "pool-probe", Name = "pool-probe" },
                SchemaFields = [new MetadataV2Field { Name = "name", Type = smallintHint ? MetadataV2FieldType.Integer : MetadataV2FieldType.String }]
            },
            new FeatureStorageMapping(tableName, SchemaName: schemaName, PrimaryKeyColumn: "id", GeometryColumn: null,
                ProviderOptions: smallintHint ? new Dictionary<string, string>
                {
                    [FeatureStorageMapping.SourceBackedOption] = "true",
                    ["postgresSmallintColumn:name"] = "true"
                } : null),
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

        public Harness(NpgsqlDataSource defaultSource, string? primarySchema = null, string? requestSchema = null,
            bool multiplexing = false, bool admissionEnabled = true)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Schema"] = primarySchema,
                ["Limits:Connections:Multiplexing"] = multiplexing.ToString(),
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
            var schemaContext = Substitute.For<ISchemaContext>();
            schemaContext.CurrentSchema.Returns(requestSchema);
            Primary = new CachingDatabaseConnectionProvider(defaultSource,
                NullLogger<CachingDatabaseConnectionProvider>.Instance,
                schemaContext: schemaContext,
                activeDbConnectionTracker: Tracker, concurrencyGate: admissionEnabled ? Gate : null, connectionPoolMetrics: Metrics);
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
