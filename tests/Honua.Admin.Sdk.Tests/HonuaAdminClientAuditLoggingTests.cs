// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Admin.Sdk;
using Honua.Admin.Sdk.Clients;
using Honua.Admin.Sdk.Models;
using Honua.Admin.Sdk.Services;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Transport.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using DomainFeature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Admin.Sdk.Tests;

public sealed class HonuaAdminClientAuditLoggingTests
{
    [Fact]
    public async Task DeployServiceAsync_RedactsSensitiveConfigurationFromAuditLogs()
    {
        var logger = new ListLogger<HonuaAdminClient>();
        using var client = new HonuaAdminClient(
            new StubFeatureClient(),
            new StubServiceManagementClient(),
            new StubUserManagementClient(),
            new StubBulkOperationsClient(),
            new StubMonitoringClient(),
            Options.Create(new HonuaAdminClientOptions()),
            logger);

        var configuration = new ServiceConfiguration
        {
            Name = "coastal-observations",
            DataSource = "Host=db.example.com;Username=app;Password=super-secret-password",
            Layers =
            {
                new LayerConfiguration
                {
                    Name = "shoreline",
                    TableName = "shoreline",
                    GeometryColumn = "geom",
                    SpatialReference = 4326,
                    Settings = new Dictionary<string, object>
                    {
                        ["apiToken"] = "very-secret-token"
                    }
                }
            },
            Settings = new Dictionary<string, object>
            {
                ["connectionString"] = "postgres://contains-secret"
            }
        };

        await client.DeployServiceAsync(configuration, AdminContext.System());

        var logOutput = string.Join(Environment.NewLine, logger.Messages);
        Assert.Contains("[REDACTED]", logOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-password", logOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("very-secret-token", logOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("contains-secret", logOutput, StringComparison.Ordinal);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class StubFeatureClient : IFeatureServiceClient<AdminContext>
    {
        public Task<QueryResult<DomainFeature>> QueryFeaturesAsync(string serviceId, int layerId, FeatureQuery query, AdminContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(QueryResult<DomainFeature>.Empty());

        public async IAsyncEnumerable<FeaturePage> QueryFeaturesStreamAsync(string serviceId, int layerId, FeatureQuery query, AdminContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new FeaturePage
            {
                Features = ImmutableArray<DomainFeature>.Empty,
                IsLastPage = true,
                PageNumber = 0
            };
            await Task.CompletedTask;
        }

        public Task<EditResult> ApplyEditsAsync(string serviceId, int layerId, FeatureEdits edits, AdminContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new EditResult());
    }

    private sealed class StubServiceManagementClient : IServiceManagementClient
    {
        public Task<IEnumerable<ServiceInfo>> GetServicesAsync(AdminContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<ServiceInfo>>([]);

        public Task<ServiceDetails> GetServiceDetailsAsync(string serviceId, AdminContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new ServiceDetails());

        public Task<ServiceDeploymentResult> DeployServiceAsync(ServiceConfiguration configuration, AdminContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new ServiceDeploymentResult
            {
                Success = true,
                ServiceId = configuration.Name,
                Message = "ok"
            });

        public void Dispose()
        {
        }
    }

    private sealed class StubUserManagementClient : IUserManagementClient
    {
        public Task<UserCreationResult> CreateUserAsync(UserCreateRequest request, AdminContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new UserCreationResult());

        public Task<PermissionResult> GrantServiceAccessAsync(string username, string serviceId, PermissionLevel permissionLevel, AdminContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new PermissionResult());

        public void Dispose()
        {
        }
    }

    private sealed class StubBulkOperationsClient : IBulkOperationsClient
    {
        public async IAsyncEnumerable<AdminProgress> ImportDataAsync(Stream dataStream, BulkImportOptions options, AdminContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AdminProgress();
            await Task.CompletedTask;
        }

        public Task<Stream> ExportServiceDataAsync(string serviceId, BulkExportOptions options, AdminContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream());

        public void Dispose()
        {
        }
    }

    private sealed class StubMonitoringClient : IMonitoringClient
    {
        public Task<ServiceHealth> GetServiceHealthAsync(string serviceId, AdminContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new ServiceHealth());

        public Task<PerformanceMetrics> GetServiceMetricsAsync(string serviceId, TimeSpan timeRange, AdminContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new PerformanceMetrics());

        public void Dispose()
        {
        }
    }
}
