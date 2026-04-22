// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text;
using System.Collections.Concurrent;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.FeatureServer;
using Honua.Core.Queries.Filters;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests;

/// <summary>
/// WebApplicationFactory configured for the test environment without Aspire wiring.
/// </summary>
public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.ConfigureServices(services =>
        {
            services.AddScoped<IDatabaseHealthChecker, Honua.TestKit.Infrastructure.MockHealthyDatabaseChecker>();
            services.AddScoped<TestFeatureStore>();
            services.AddScoped<IFeatureReader>(provider => provider.GetRequiredService<TestFeatureStore>());
            services.AddScoped<IFeatureWriter>(provider => provider.GetRequiredService<TestFeatureStore>());
            services.AddScoped<ITileProvider>(provider => provider.GetRequiredService<TestFeatureStore>());
            services.AddScoped<IRelationshipStore>(provider => provider.GetRequiredService<TestFeatureStore>());
            services.AddScoped<IStreamingFeatureStore>(provider => provider.GetRequiredService<TestFeatureStore>());
            services.RemoveAll<IChangeTracker>();
            services.RemoveAll<IReplicaStore>();
            services.AddSingleton<IChangeTracker, InMemoryChangeTracker>();
            services.AddSingleton<IReplicaStore, InMemoryReplicaStore>();
            services.AddScoped<ILayerCatalog>(_ => new TestLayerCatalog());
            services.AddScoped<ISecureConnectionRegistry, NullSecureConnectionRegistry>();
            services.AddScoped<IConnectionEncryptionService, NullConnectionEncryptionService>();
            services.AddScoped<ISecureConnectionResolver, NullSecureConnectionResolver>();
            services.AddScoped<ILayerPublishingService, NullLayerPublishingService>();
            services.AddSingleton<IDatabaseMigrationRunner, NullDatabaseMigrationRunner>();
            services.AddSingleton<IMetadataResourceStore, InMemoryMetadataResourceStore>();
            services.AddSingleton<IManifestVersionStore, InMemoryManifestVersionStore>();
            services.AddSingleton<IDatabaseConnectionStringBuilder, TestDatabaseConnectionStringBuilder>();
            services.AddSingleton<ILeaderElectionStrategy, NoOpLeaderElectionStrategy>();
            services.AddScoped<IAlertAdminStore, NullAlertAdminStore>();
            services.AddScoped<ISqlFilterTranslator, AllowAllSqlFilterTranslator>();
        });
        builder.ConfigureAppConfiguration((context, configBuilder) =>
        {
            var attachmentsPath = Path.Combine(Path.GetTempPath(), "honua-test-storage");
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alerts:Enabled"] = "false",
                ["ConnectionStrings:DefaultConnection"] = TestConnectionStrings.DefaultPostgresConnectionString,
                ["ConnectionStrings:honua"] = TestConnectionStrings.DefaultPostgresConnectionString,
                ["HONUA_SKIP_MIGRATIONS"] = "true",
                ["HONUA_SERVE_API_DOCS"] = "true",
                ["FileStorage:Provider"] = "Local",
                ["FileStorage:LocalStorage:BasePath"] = attachmentsPath,
                ["FileStorage:LocalStorage:CreateDirectoryIfNotExists"] = "true"
            });
        });
    }

    private sealed class NullSecureConnectionRegistry : ISecureConnectionRegistry
    {
        public Task<DataConnection> CreateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default)
            => Task.FromResult(connection);

        public Task RegisterConnectionAsync(DataConnection connection)
            => Task.CompletedTask;

        public Task<DataConnection?> GetConnectionAsync(string connectionId)
            => Task.FromResult<DataConnection?>(null);

        public Task<DataConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken)
            => Task.FromResult<DataConnection?>(null);

        public Task<DataConnection?> GetConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult<DataConnection?>(null);

        public Task<DataConnection?> GetConnectionByNameAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult<DataConnection?>(null);

        public Task<IEnumerable<DataConnection>> GetAllConnectionsAsync()
            => Task.FromResult<IEnumerable<DataConnection>>(Array.Empty<DataConnection>());

        public Task<IEnumerable<DataConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<DataConnection>>(Array.Empty<DataConnection>());

        public Task<DataConnection> UpdateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default)
            => Task.FromResult(connection);

        public Task<bool> RemoveConnectionAsync(string connectionId)
            => Task.FromResult(false);

        public Task<bool> DeleteConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<Dictionary<string, ConnectionHealthStatus>> TestAllConnectionsAsync()
            => Task.FromResult(new Dictionary<string, ConnectionHealthStatus>());

        public Task UpdateHealthStatusAsync(string connectionId, bool isHealthy, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NullConnectionEncryptionService : IConnectionEncryptionService
    {
        public Task<byte[]> EncryptConnectionStringAsync(string connectionString)
            => Task.FromResult(Encoding.UTF8.GetBytes(connectionString ?? string.Empty));

        public Task<string> DecryptConnectionStringAsync(byte[] encryptedData, int keyVersion)
            => Task.FromResult(Encoding.UTF8.GetString(encryptedData ?? Array.Empty<byte>()));

        public Task<int> GetCurrentKeyVersionAsync() => Task.FromResult(1);

        public Task<int> RotateKeyAsync() => throw new NotSupportedException(
            "In-place key rotation is not supported.");

        public Task<bool> ValidateEncryptionAsync() => Task.FromResult(true);
    }

    private sealed class NullSecureConnectionResolver : ISecureConnectionResolver
    {
        public Task<string> ResolveConnectionStringAsync(string connectionName, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);

        public Task<string> ResolveConnectionStringAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);

        public Task<bool> TestConnectionHealthAsync(string connectionName, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<string>> GetAvailableConnectionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class TestDatabaseConnectionStringBuilder : IDatabaseConnectionStringBuilder
    {
        public string BuildConnectionString(
            string host,
            int port,
            string databaseName,
            string username,
            string password,
            SslMode sslMode)
        {
            return $"Host={host};Port={port};Database={databaseName};Username={username};Password={password};SslMode={sslMode}";
        }

        public Task<string> BuildConnectionStringAsync(DataConnection connection)
        {
            var connectionString = BuildConnectionString(
                connection.Host,
                connection.Port,
                connection.DatabaseName,
                connection.Username,
                password: string.Empty,
                connection.SslMode);
            return Task.FromResult(connectionString);
        }

        public bool ValidateConnectionString(string connectionString)
            => !string.IsNullOrWhiteSpace(connectionString);
    }

    private sealed class NoOpLeaderElectionStrategy : ILeaderElectionStrategy
    {
        public bool IsLeader => false;

        public string InstanceId => "test";

        public Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task ReleaseAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NullAlertAdminStore : IAlertAdminStore
    {
        public Task<IReadOnlyList<AlertZoneDefinition>> ListZonesAsync(string? serviceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AlertZoneDefinition>>(Array.Empty<AlertZoneDefinition>());

        public Task<AlertZoneDefinition?> GetZoneAsync(long zoneId, CancellationToken cancellationToken = default)
            => Task.FromResult<AlertZoneDefinition?>(null);

        public Task<AlertZoneDefinition> CreateZoneAsync(AlertZoneDefinition zone, CancellationToken cancellationToken = default)
            => Task.FromException<AlertZoneDefinition>(new NotSupportedException("Alerts are not available in this test fixture."));

        public Task<AlertZoneDefinition?> UpdateZoneAsync(AlertZoneDefinition zone, CancellationToken cancellationToken = default)
            => Task.FromException<AlertZoneDefinition?>(new NotSupportedException("Alerts are not available in this test fixture."));

        public Task<bool> DeleteZoneAsync(long zoneId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<AlertRuleDefinition>> ListRulesAsync(string? serviceId, int? layerId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AlertRuleDefinition>>(Array.Empty<AlertRuleDefinition>());

        public Task<AlertRuleDefinition> CreateRuleAsync(AlertRuleDefinition rule, CancellationToken cancellationToken = default)
            => Task.FromException<AlertRuleDefinition>(new NotSupportedException("Alerts are not available in this test fixture."));

        public Task<AlertRuleDefinition?> UpdateRuleAsync(AlertRuleDefinition rule, CancellationToken cancellationToken = default)
            => Task.FromException<AlertRuleDefinition?>(new NotSupportedException("Alerts are not available in this test fixture."));

        public Task<bool> DeleteRuleAsync(long ruleId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class InMemoryChangeTracker : IChangeTracker
    {
        public Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(1L);

        public Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
            long sinceGeneration,
            int[] layerIds,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FeatureChange>>(Array.Empty<FeatureChange>());
    }

    private sealed class InMemoryReplicaStore : IReplicaStore
    {
        private readonly ConcurrentDictionary<string, ReplicaState> _replicas = new(StringComparer.OrdinalIgnoreCase);

        public Task SetAsync(ReplicaState replica, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _replicas[replica.ReplicaId] = replica;
            return Task.CompletedTask;
        }

        public Task<ReplicaState?> GetAsync(string replicaId, CancellationToken cancellationToken = default)
        {
            _replicas.TryGetValue(replicaId, out var replica);
            return Task.FromResult(replica);
        }

        public Task<bool> RemoveAsync(string replicaId, CancellationToken cancellationToken = default)
            => Task.FromResult(_replicas.TryRemove(replicaId, out _));
    }

    private sealed class AllowAllSqlFilterTranslator : ISqlFilterTranslator
    {
        public SqlFragment Translate(FilterExpression filter, Core.Features.Catalog.Domain.LayerDefinition layer)
            => new("1=1", Array.Empty<object?>());
    }

    private sealed class NullLayerPublishingService : ILayerPublishingService
    {
        public Task<IReadOnlyList<PublishedLayerSummary>> ListPublishedLayersAsync(
            string connectionString,
            string serviceName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<PublishedLayerSummary>>(Array.Empty<PublishedLayerSummary>());
        }

        public Task<PublishedLayerSummary> PublishLayerAsync(
            string connectionString,
            LayerPublishRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Layer publishing is not available in this test fixture.");
        }

        public Task<PublishedLayerSummary?> SetLayerEnabledAsync(
            string connectionString,
            int layerId,
            string serviceName,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<PublishedLayerSummary?>(null);
        }

        public Task<IReadOnlyList<PublishedLayerSummary>> SetServiceLayersEnabledAsync(
            string connectionString,
            string serviceName,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<PublishedLayerSummary>>(Array.Empty<PublishedLayerSummary>());
        }
    }

    private sealed class NullDatabaseMigrationRunner : IDatabaseMigrationRunner
    {
        public Task<DatabaseMigrationPlan> PlanMigrationsAsync(
            string connectionString,
            Assembly migrationsAssembly,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(DatabaseMigrationPlan.Succeeded());
        }

        public Task<DatabaseMigrationResult> RunMigrationsAsync(
            string connectionString,
            Assembly migrationsAssembly,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(DatabaseMigrationResult.Succeeded());
        }
    }
}
