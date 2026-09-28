// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Infrastructure.Caching;
using Honua.Db.Postgres.Features.Security;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.Security;

public partial class SecureConnectionRegistryTests
{
    [SecurityTest]
    [Fact]
    public async Task RegistryBinding_RotationAndDeletion_RetirePoolsWithoutInterruptingLeases()
    {
        var settings = new NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString)
        {
            ApplicationName = "pool-lifecycle-" + Guid.NewGuid().ToString("N")
        };
        var sourceString = settings.ConnectionString;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Limits:Connections:MinConnectionPoolSize"] = "1",
            ["Limits:Connections:MaxConnectionPoolSize"] = "2",
            ["Limits:Connections:ConnectionIdleLifetimeSeconds"] = "2",
            ["Limits:Connections:ConnectionPruningIntervalSeconds"] = "1"
        }).Build();
        using var local = new SecureConnectionDataSourceCache(configuration);
        using var remote = new SecureConnectionDataSourceCache(configuration);
        var registry = new PostgresSecureConnectionRegistry(
            _fixture.GetService<IPrimaryDatabaseConnectionProvider>(),
            NullLogger<PostgresSecureConnectionRegistry>.Instance, local);
        var created = await registry.CreateConnectionAsync(DataConnection.CreateWithEncryptedCredentials(
            "pool-lifecycle-" + Guid.NewGuid().ToString("N"), settings.Host!, settings.Port,
            settings.Database!, settings.Username!, [1, 2, 3], 1, "test", sslRequired: false,
            sslMode: Honua.Core.Features.Security.Domain.SslMode.Disable));
        var id = created.ConnectionId.ToString("D");
        Assert.Equal(id, created.Id);

        try
        {
            var binding = await ResolveRegistryBindingAsync(registry, id);
            Assert.Equal(id, binding.Connection!.Id);
            Assert.Equal(created.ConnectionId, binding.Connection.ConnectionId);
            var updated = await registry.UpdateConnectionAsync(binding.Connection);
            Assert.Equal(id, updated.Id);

            await using var primarySource = NpgsqlDataSource.Create(_fixture.Postgres.ConnectionString);
            using var primary = new CachingDatabaseConnectionProvider(primarySource,
                NullLogger<CachingDatabaseConnectionProvider>.Instance);
            var bound = new PostgresBoundConnectionProvider(local, primary);
            using var initialPin = local.Acquire("bound-id:" + id, sourceString, preservePrimarySchema: false);
            await using var first = await bound.OpenConnectionAsync(binding.Connection.Id, sourceString);
            await using (var query = new NpgsqlCommand("SELECT 1", first))
            {
                Assert.Equal(1, await query.ExecuteScalarAsync());
            }

            settings.Timeout = settings.Timeout == 9 ? 10 : 9;
            var rotatedString = settings.ConnectionString;
            await using var current = await bound.OpenConnectionAsync(binding.Connection.Id, rotatedString);
            initialPin.Dispose();
            await first.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            {
                await using var invalid = await initialPin.DataSource.OpenConnectionAsync(new CancellationToken(true));
            });

            using var named = local.Acquire(created.Name, rotatedString);
            await WarmPoolAsync(named.DataSource);
            using var otherInstance = remote.Acquire(created.Name, rotatedString);
            await WarmPoolAsync(otherInstance.DataSource);
            named.Dispose();
            otherInstance.Dispose();
            Assert.True(await registry.DeleteConnectionAsync(created.ConnectionId));
            Assert.Null(await registry.GetConnectionAsync(created.ConnectionId));
            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            {
                await using var invalid = await named.DataSource.OpenConnectionAsync(new CancellationToken(true));
            });

            await using var observer = await primarySource.OpenConnectionAsync();
            // The remote idle source must close even though its minimum pool size is one.
            // The local bound connection remains pinned and usable throughout retirement.
            await WaitForSourceBackendsAsync(observer, settings.ApplicationName!, expected: 1);
            await using (var query = new NpgsqlCommand("SELECT 42", current))
            {
                Assert.Equal(42, await query.ExecuteScalarAsync());
            }
            await current.DisposeAsync();
            await WaitForSourceBackendsAsync(observer, settings.ApplicationName!, expected: 0);

            // A failed/missing deletion must not retire an unrelated registered pool.
            using var unrelated = local.Acquire("unrelated", rotatedString);
            Assert.False(await registry.DeleteConnectionAsync(Guid.NewGuid()));
            await WarmPoolAsync(unrelated.DataSource);
            local.Dispose();
            await using var primaryQuery = new NpgsqlCommand("SELECT 42", observer);
            Assert.Equal(42, await primaryQuery.ExecuteScalarAsync());
        }
        finally
        {
            await registry.DeleteConnectionAsync(created.ConnectionId);
        }
    }

    private static async Task WarmPoolAsync(NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var query = new NpgsqlCommand("SELECT 1", connection);
        Assert.Equal(1, await query.ExecuteScalarAsync());
    }

    private static async Task WaitForSourceBackendsAsync(NpgsqlConnection observer, string applicationName, long expected)
    {
        var timeout = Stopwatch.StartNew();
        long observed;
        do
        {
            await using var query = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE application_name = $1", observer);
            query.Parameters.AddWithValue(applicationName);
            observed = (long)(await query.ExecuteScalarAsync())!;
            if (observed == expected)
            {
                return;
            }
            await Task.Delay(50);
        } while (timeout.Elapsed < TimeSpan.FromSeconds(15));
        Assert.Equal(expected, observed);
    }

    private static async Task<FeatureProviderBinding> ResolveRegistryBindingAsync(
        PostgresSecureConnectionRegistry registry, string connectionId)
    {
        var service = new MetadataV2Service { Metadata = new() { Id = "svc", Name = "svc" } };
        var resource = new MetadataV2Resource
        {
            Metadata = new() { Id = "res", Name = "res" },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds = ["binding"],
            SchemaFields = [new() { Name = "id", Type = MetadataV2FieldType.BigInteger, SemanticRoles = ["id.primary"] }]
        };
        var storage = new MetadataV2StorageBinding
        {
            Metadata = new() { Id = "binding", Name = "binding" }, ResourceId = "res", ConnectionId = connectionId,
            StorageType = MetadataV2StorageType.RelationalTable, Locator = "public.proof", StorageLayerId = 1
        };
        var publication = new MetadataV2Publication
        {
            Metadata = new() { Id = "pub", Name = "pub" }, ServiceId = "svc", ResourceId = "res",
            StorageBindingId = "binding", Identifier = new() { Value = "1", IsNumeric = true }
        };
        var graph = new MetadataV2Graph
        {
            Revision = 1, Environment = "test", Services = [service], Resources = [resource],
            StorageBindings = [storage], Publications = [publication],
            Connections = [new() { Metadata = new() { Id = connectionId, Name = "source" }, Provider = DataProviderNames.Postgis }]
        };
        var provider = Substitute.For<IFeatureDataProvider>();
        provider.ProviderName.Returns(DataProviderNames.Postgis);
        provider.Capabilities.Returns(FeatureProviderCapabilities.ReadWritePostgis);
        var router = new FeatureProviderQueryRouter(registry, new FeatureDataProviderRegistry([provider]));
        return await router.ResolveBindingAsync(new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.UtcNow),
            service, resource, publication, 1, FeatureProviderReadOperation.Query);
    }
}
