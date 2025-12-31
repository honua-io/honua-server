// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Decorator for ILayerCatalog that records catalog metadata query metrics.
/// </summary>
internal sealed class MonitoredLayerCatalogDecorator : ILayerCatalog
{
    private readonly ILayerCatalog _innerCatalog;
    private readonly IPerformanceMonitor _performanceMonitor;
    private readonly ILogger<MonitoredLayerCatalogDecorator> _logger;

    public MonitoredLayerCatalogDecorator(
        ILayerCatalog innerCatalog,
        IPerformanceMonitor performanceMonitor,
        ILogger<MonitoredLayerCatalogDecorator> logger)
    {
        _innerCatalog = innerCatalog ?? throw new ArgumentNullException(nameof(innerCatalog));
        _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
        => MonitorAsync("layer", "get", ct => _innerCatalog.GetLayerAsync(layerId, ct), result => result is null ? 0 : 1, cancellationToken);

    public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
        => MonitorAsync("layer", "list", ct => _innerCatalog.ListLayersAsync(ct), result => result.Length, cancellationToken);

    public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        => MonitorAsync("service", "get", ct => _innerCatalog.GetServiceAsync(serviceName, ct), result => result is null ? 0 : 1, cancellationToken);

    public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
        => MonitorAsync("service", "list", ct => _innerCatalog.ListServicesAsync(ct), result => result.Length, cancellationToken);

    public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
        => MonitorAsync("layer", "exists", ct => _innerCatalog.LayerExistsAsync(layerId, ct), result => result ? 1 : 0, cancellationToken);

    public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
        => MonitorAsync("service", "exists", ct => _innerCatalog.ServiceExistsAsync(serviceName, ct), result => result ? 1 : 0, cancellationToken);

    public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
        => MonitorAsync("relationship", "get", ct => _innerCatalog.GetRelationshipAsync(layerId, relationshipId, ct), result => result is null ? 0 : 1, cancellationToken);

    public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
        => MonitorAsync("relationship", "list", ct => _innerCatalog.ListRelationshipsAsync(layerId, ct), result => result.Length, cancellationToken);

    private async Task<T> MonitorAsync<T>(
        string catalogType,
        string operation,
        Func<CancellationToken, Task<T>> action,
        Func<T, int>? countSelector,
        CancellationToken cancellationToken)
    {
        var tags = new Dictionary<string, string>
        {
            { "catalog", catalogType },
            { "operation", operation }
        };

        using var scope = _performanceMonitor.StartOperation($"catalog_{catalogType}_{operation}")
            .WithTag("catalog", catalogType)
            .WithTag("operation", operation);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            _performanceMonitor.RecordHistogram("honua_catalog_query_duration_ms", stopwatch.Elapsed.TotalMilliseconds, tags);
            _performanceMonitor.RecordCounter("honua_catalog_query_total", 1, tags);

            if (countSelector is not null)
            {
                var count = countSelector(result);
                _performanceMonitor.RecordHistogram("honua_catalog_query_items", count, tags);
            }

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            scope.WithTag("error", ex.GetType().Name);

            _performanceMonitor.RecordHistogram("honua_catalog_query_duration_ms", stopwatch.Elapsed.TotalMilliseconds, tags);
            _performanceMonitor.RecordCounter("honua_catalog_query_total", 1, tags);
            _performanceMonitor.RecordCounter("honua_catalog_query_failures_total", 1, tags);

            MonitoredLayerCatalogLog.CatalogOperationFailed(_logger, catalogType, operation, ex);
            throw;
        }
    }
}

internal static partial class MonitoredLayerCatalogLog
{
    [LoggerMessage(
        EventId = 7450,
        Level = LogLevel.Error,
        Message = "Catalog {CatalogType} {Operation} failed")]
    public static partial void CatalogOperationFailed(ILogger logger, string catalogType, string operation, Exception exception);
}
