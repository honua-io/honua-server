// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Infrastructure.Caching;
using Honua.Db.Postgres.Features.Security;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.Security;

[Collection("Database")]
public sealed class SecureConnectionAwareDatabaseProviderLifetimeTests(PostgresFixture fixture)
{
    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NamedConnection_WithoutAdmission_RotationRetainsPoolUntilConnectionDisposal(
        bool multiplexing, bool asyncDisposal)
    {
        const string connectionName = "named-lifetime";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:SecureConnection:Name"] = connectionName,
            ["Limits:Connections:Multiplexing"] = multiplexing.ToString(),
            ["Limits:Connections:MinConnectionPoolSize"] = "0"
        }).Build();
        using var cache = new SecureConnectionDataSourceCache(configuration);
        using var primary = new CachingDatabaseConnectionProvider(fixture.DataSource,
            NullLogger<CachingDatabaseConnectionProvider>.Instance);
        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver.ResolveConnectionStringAsync(connectionName, Arg.Any<CancellationToken>())
            .Returns(fixture.ConnectionString);
        using var provider = new SecureConnectionAwareDatabaseProvider(primary, resolver, cache, configuration,
            NullLogger<SecureConnectionAwareDatabaseProvider>.Instance);
        using var initial = cache.Acquire(connectionName, fixture.ConnectionString);
        var oldSource = initial.DataSource;
        initial.Dispose();

        await using var connection = await provider.OpenConnectionAsync();
        connection.Should().BeOfType<SemaphoreReleasingConnection>("a pool pin needs an owner even without admission");
        using var replacement = cache.Acquire(connectionName,
            new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { ApplicationName = "named-rotated" }.ConnectionString);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            (await command.ExecuteScalarAsync()).Should().Be(1);
        }

        if (asyncDisposal)
        {
            await connection.DisposeAsync();
        }
        else
        {
            connection.Dispose();
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            // A bootstrapped multiplexed pool may return a logical connection
            // after disposal; a transaction forces physical connector binding.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var unexpected = await oldSource.OpenConnectionAsync(timeout.Token);
            await using var transaction = await unexpected.BeginTransactionAsync(timeout.Token);
        });
    }
}
