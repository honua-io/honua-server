// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
            services.AddScoped<ILayerCatalog>(_ => new TestLayerCatalog());
            services.AddScoped<ISecureConnectionRegistry, NullSecureConnectionRegistry>();
            services.AddScoped<IConnectionEncryptionService, NullConnectionEncryptionService>();
            services.AddScoped<ISecureConnectionResolver, NullSecureConnectionResolver>();
            services.AddScoped<ILayerPublishingService, NullLayerPublishingService>();
            services.AddSingleton<IDatabaseMigrationRunner, NullDatabaseMigrationRunner>();
            services.AddSingleton<IMetadataResourceStore, InMemoryMetadataResourceStore>();
            services.AddSingleton<IDatabaseConnectionStringBuilder, TestDatabaseConnectionStringBuilder>();
        });
        builder.ConfigureAppConfiguration((context, configBuilder) =>
        {
            var attachmentsPath = Path.Combine(Path.GetTempPath(), "honua-test-storage");
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test;Username=test;Password=test",
                ["HONUA_SKIP_MIGRATIONS"] = "true",
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

        public Task<DataConnection?> GetConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult<DataConnection?>(null);

        public Task<DataConnection?> GetConnectionByNameAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult<DataConnection?>(null);

        public Task<IReadOnlyList<DataConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DataConnection>>(Array.Empty<DataConnection>());

        public Task<DataConnection> UpdateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default)
            => Task.FromResult(connection);

        public Task<bool> DeleteConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<bool> TestConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<bool> UpdateHealthStatusAsync(Guid connectionId, ConnectionHealthStatus healthStatus, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class NullConnectionEncryptionService : IConnectionEncryptionService
    {
        public Task<byte[]> EncryptConnectionStringAsync(string connectionString)
            => Task.FromResult(Encoding.UTF8.GetBytes(connectionString ?? string.Empty));

        public Task<string> DecryptConnectionStringAsync(byte[] encryptedData, int keyVersion)
            => Task.FromResult(Encoding.UTF8.GetString(encryptedData ?? Array.Empty<byte>()));

        public Task<int> GetCurrentKeyVersionAsync() => Task.FromResult(1);

        public Task<int> RotateKeyAsync() => Task.FromResult(1);

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
        public Task<DatabaseMigrationResult> RunMigrationsAsync(
            string connectionString,
            Assembly migrationsAssembly,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(DatabaseMigrationResult.Succeeded());
        }
    }
}
