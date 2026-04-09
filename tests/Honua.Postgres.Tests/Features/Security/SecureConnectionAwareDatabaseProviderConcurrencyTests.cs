// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Security.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Honua.Postgres.Features.Infrastructure.Caching;
using Honua.Postgres.Features.Security;
using Honua.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Security;

/// <summary>
/// Regression tests for the interaction between
/// <see cref="SecureConnectionAwareDatabaseProvider"/> and the shared
/// <see cref="QueryConcurrencyGate"/> when secure-mode is configured.
/// </summary>
/// <remarks>
/// Ticket #605: a previous fix pass took the gate slot before resolving the
/// secure connection. The registry's metadata lookup itself opens a connection
/// through the primary (gated) provider, so the slot acquired by the secure
/// provider self-blocked the metadata open at <c>MaxConcurrentQueries=1</c> and
/// double-counted capacity at higher limits. The fix moves the gate acquisition
/// to <em>after</em> the registry lookup completes.
/// </remarks>
[Collection("Database")]
public sealed class SecureConnectionAwareDatabaseProviderConcurrencyTests
{
    private readonly PostgresFixture _fixture;

    public SecureConnectionAwareDatabaseProviderConcurrencyTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task OpenConnectionAsync_SecureMode_DoesNotSelfBlockWhenMaxConcurrentQueriesIsOne()
    {
        // Arrange — production wiring with the smallest possible gate so that
        // any double-acquisition would manifest as a 503 timeout.
        var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 1,
            ConnectionAcquisitionTimeoutSeconds = 2
        });

        using var primaryProvider = new CachingDatabaseConnectionProvider(
            _fixture.DataSource,
            NullLogger<CachingDatabaseConnectionProvider>.Instance,
            concurrencyGate: gate);

        // Stub resolver that mimics PostgresSecureConnectionRegistry: it opens
        // a metadata connection through the primary (gated) provider before
        // returning the resolved connection string. This is the exact path
        // that previously self-deadlocked.
        var resolver = new RegistryLookupSimulatingResolver(
            primaryProvider,
            _fixture.ConnectionString,
            gate);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:SecureConnection:Name"] = "regression-605"
            })
            .Build();

        using var dataSourceCache = new SecureConnectionDataSourceCache(configuration);

        using var secureProvider = new SecureConnectionAwareDatabaseProvider(
            primaryProvider,
            resolver,
            dataSourceCache,
            configuration,
            NullLogger<SecureConnectionAwareDatabaseProvider>.Instance,
            schemaContext: null,
            activeDbConnectionTracker: null,
            concurrencyGate: gate);

        // Act — this would have raised ServiceUnavailableException pre-fix
        // because the secure provider held the only slot while the registry
        // lookup tried to take a second slot via the primary provider.
        await using var connection = await secureProvider.OpenConnectionAsync(CancellationToken.None);

        // Assert — the connection opened, the resolver actually ran, and the
        // gate was NOT held during the resolver call (so the metadata open
        // could acquire the only slot without self-blocking).
        connection.Should().NotBeNull();
        connection.State.Should().Be(System.Data.ConnectionState.Open);

        resolver.ResolveCallCount.Should().Be(1);
        resolver.SlotsAvailableBeforeMetadataOpen.Should().Be(
            1,
            "the secure provider must not hold the gate slot while the registry resolves the connection");
        resolver.MetadataOpenSucceeded.Should().BeTrue(
            "the registry's metadata open must not self-deadlock at MaxConcurrentQueries=1");

        // The gate slot is currently held by the returned secure connection.
        gate.AvailableSlots.Should().Be(0, "the secure connection wrapper holds the slot until disposed");

        // Dispose the connection and verify the slot is released exactly once.
        await connection.DisposeAsync();
        gate.AvailableSlots.Should().Be(1, "disposing the secure connection must release exactly one slot");
    }

    [Fact]
    public async Task OpenConnectionAsync_SecureMode_DoesNotDoubleCountSlotsAtHigherLimits()
    {
        // Arrange — small but >1 limit so we can prove that a single secure
        // open consumes exactly one slot, not two (the second symptom of the
        // original double-acquisition bug).
        var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 2,
            ConnectionAcquisitionTimeoutSeconds = 2
        });

        using var primaryProvider = new CachingDatabaseConnectionProvider(
            _fixture.DataSource,
            NullLogger<CachingDatabaseConnectionProvider>.Instance,
            concurrencyGate: gate);

        var resolver = new RegistryLookupSimulatingResolver(
            primaryProvider,
            _fixture.ConnectionString,
            gate);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:SecureConnection:Name"] = "regression-605"
            })
            .Build();

        using var dataSourceCache = new SecureConnectionDataSourceCache(configuration);

        using var secureProvider = new SecureConnectionAwareDatabaseProvider(
            primaryProvider,
            resolver,
            dataSourceCache,
            configuration,
            NullLogger<SecureConnectionAwareDatabaseProvider>.Instance,
            schemaContext: null,
            activeDbConnectionTracker: null,
            concurrencyGate: gate);

        // Act
        await using var connection = await secureProvider.OpenConnectionAsync(CancellationToken.None);

        // Assert — peak consumption during the secure open was exactly one slot
        // (the metadata open's slot), not two. Pre-fix this would have been 1
        // because the secure provider already held a slot when the resolver ran,
        // leaving the metadata open to consume the second slot for a peak of two.
        resolver.SlotsAvailableBeforeMetadataOpen.Should().Be(
            2,
            "the secure provider must not pre-acquire a gate slot — peak consumption per secure open must be one slot, not two");

        // Steady state: exactly one slot is held by the open secure connection.
        connection.Should().NotBeNull();
        gate.AvailableSlots.Should().Be(
            1,
            "a single secure open must consume exactly one slot in steady state");

        await connection.DisposeAsync();
        gate.AvailableSlots.Should().Be(2);
    }

    /// <summary>
    /// Test stub that mirrors <see cref="PostgresSecureConnectionRegistry.GetConnectionByNameAsync"/>:
    /// the resolver opens a metadata connection through the primary provider
    /// (which is gated by the same <see cref="QueryConcurrencyGate"/>) before
    /// returning the resolved connection string.
    /// </summary>
    private sealed class RegistryLookupSimulatingResolver : ISecureConnectionResolver
    {
        private readonly IPrimaryDatabaseConnectionProvider _primaryProvider;
        private readonly string _connectionString;
        private readonly QueryConcurrencyGate _gate;

        public int ResolveCallCount;
        public int SlotsAvailableBeforeMetadataOpen = -1;
        public bool MetadataOpenSucceeded;

        public RegistryLookupSimulatingResolver(
            IPrimaryDatabaseConnectionProvider primaryProvider,
            string connectionString,
            QueryConcurrencyGate gate)
        {
            _primaryProvider = primaryProvider;
            _connectionString = connectionString;
            _gate = gate;
        }

        public async Task<string> ResolveConnectionStringAsync(string connectionName, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ResolveCallCount);

            // Capture gate state before the metadata open. Pre-fix this would
            // have been 0 because the secure provider already held the slot.
            SlotsAvailableBeforeMetadataOpen = _gate.AvailableSlots;

            // Mirror PostgresSecureConnectionRegistry.GetConnectionByNameAsync —
            // open a metadata connection through the primary (gated) provider.
            await using var metadataConnection = await _primaryProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            MetadataOpenSucceeded = true;

            return _connectionString;
        }

        public Task<string> ResolveConnectionStringAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> TestConnectionHealthAsync(string connectionName, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<string>> GetAvailableConnectionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
