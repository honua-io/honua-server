using System.Diagnostics;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Tiles.Domain;

namespace Honua.Postgres.Features.FeatureStore;

/// <summary>
/// Decorator for IFeatureStore that adds comprehensive performance monitoring and telemetry.
/// </summary>
/// <remarks>
/// This decorator wraps any IFeatureStore implementation to provide detailed performance metrics
/// including query timing, record counts, cache metrics, and database operation tracking.
/// </remarks>
internal sealed class MonitoredFeatureStoreDecorator : IFeatureStore
{
    private readonly IFeatureStore _innerStore;
    private readonly IPerformanceMonitor _performanceMonitor;
    private readonly ILogger<MonitoredFeatureStoreDecorator> _logger;

    public MonitoredFeatureStoreDecorator(
        IFeatureStore innerStore,
        IPerformanceMonitor performanceMonitor,
        ILogger<MonitoredFeatureStoreDecorator> logger)
    {
        _innerStore = innerStore ?? throw new ArgumentNullException(nameof(innerStore));
        _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IAsyncEnumerable<Feature>> QueryAsync(
        LayerDefinition layerDefinition,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        using var scope = _performanceMonitor.StartOperation("query_features")
            .WithTag("layer_id", layerDefinition.Id.ToString())
            .WithTag("operation", "query");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _innerStore.QueryAsync(layerDefinition, query, cancellationToken);

            // Count the results as they stream for metrics
            var monitoredResult = MonitorResults(result, "query", layerDefinition.Id.ToString());

            return monitoredResult;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.QueryFailed(_logger, layerDefinition.Id.ToString(), ex.Message, ex);
            throw;
        }
        finally
        {
            MonitoredFeatureStoreLog.QueryCompleted(_logger,
                layerDefinition.Id.ToString(),
                stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public async Task<IAsyncEnumerable<Feature>> QueryOptimizedAsync(
        LayerDefinition layerDefinition,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        using var scope = _performanceMonitor.StartOperation("query_features_optimized")
            .WithTag("layer_id", layerDefinition.Id.ToString())
            .WithTag("operation", "query_optimized");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _innerStore.QueryOptimizedAsync(layerDefinition, query, cancellationToken);
            return MonitorResults(result, "query_optimized", layerDefinition.Id.ToString());
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.QueryFailed(_logger, layerDefinition.Id.ToString(), ex.Message, ex);
            throw;
        }
        finally
        {
            MonitoredFeatureStoreLog.QueryCompleted(_logger,
                layerDefinition.Id.ToString(),
                stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(
        LayerDefinition layerDefinition,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        using var scope = _performanceMonitor.StartOperation("count_features")
            .WithTag("layer_id", layerDefinition.Id.ToString())
            .WithTag("operation", "count");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _innerStore.CountAsync(layerDefinition, query, cancellationToken);

            _performanceMonitor.RecordDatabaseQuery("count", layerDefinition.Id.ToString(), stopwatch.Elapsed, 1);

            MonitoredFeatureStoreLog.CountCompleted(_logger,
                layerDefinition.Id.ToString(),
                result,
                stopwatch.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.CountFailed(_logger, layerDefinition.Id.ToString(), ex.Message, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Feature?> GetAsync(
        LayerDefinition layerDefinition,
        object id,
        string[]? outFields = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = _performanceMonitor.StartOperation("get_feature")
            .WithTag("layer_id", layerDefinition.Id.ToString())
            .WithTag("operation", "get");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _innerStore.GetAsync(layerDefinition, id, outFields, cancellationToken);

            _performanceMonitor.RecordDatabaseQuery("get", layerDefinition.Id.ToString(), stopwatch.Elapsed, result != null ? 1 : 0);

            MonitoredFeatureStoreLog.GetCompleted(_logger,
                layerDefinition.Id.ToString(),
                id.ToString()!,
                result != null,
                stopwatch.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.GetFailed(_logger, layerDefinition.Id.ToString(), id.ToString()!, ex.Message, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ApplyEditsResult> ApplyEditsAsync(
        LayerDefinition layerDefinition,
        ApplyEditsRequest request,
        CancellationToken cancellationToken = default)
    {
        using var scope = _performanceMonitor.StartOperation("apply_edits")
            .WithTag("layer_id", layerDefinition.Id.ToString())
            .WithTag("operation", "apply_edits");

        var stopwatch = Stopwatch.StartNew();
        var totalOperations = (request.Adds?.Count ?? 0) + (request.Updates?.Count ?? 0) + (request.Deletes?.Count ?? 0);

        try
        {
            var result = await _innerStore.ApplyEditsAsync(layerDefinition, request, cancellationToken);

            _performanceMonitor.RecordDatabaseQuery("apply_edits", layerDefinition.Id.ToString(), stopwatch.Elapsed, totalOperations);

            MonitoredFeatureStoreLog.ApplyEditsCompleted(_logger,
                layerDefinition.Id.ToString(),
                request.Adds?.Count ?? 0,
                request.Updates?.Count ?? 0,
                request.Deletes?.Count ?? 0,
                stopwatch.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.ApplyEditsFailed(_logger, layerDefinition.Id.ToString(), ex.Message, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Feature> CreateAsync(
        LayerDefinition layerDefinition,
        Feature feature,
        CancellationToken cancellationToken = default)
    {
        using var scope = _performanceMonitor.StartOperation("create_feature")
            .WithTag("layer_id", layerDefinition.Id.ToString())
            .WithTag("operation", "create");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _innerStore.CreateAsync(layerDefinition, feature, cancellationToken);

            _performanceMonitor.RecordDatabaseQuery("create", layerDefinition.Id.ToString(), stopwatch.Elapsed, 1);

            MonitoredFeatureStoreLog.CreateCompleted(_logger,
                layerDefinition.Id.ToString(),
                stopwatch.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.CreateFailed(_logger, layerDefinition.Id.ToString(), ex.Message, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Feature> UpdateAsync(
        LayerDefinition layerDefinition,
        Feature feature,
        CancellationToken cancellationToken = default)
    {
        using var scope = _performanceMonitor.StartOperation("update_feature")
            .WithTag("layer_id", layerDefinition.Id.ToString())
            .WithTag("operation", "update");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _innerStore.UpdateAsync(layerDefinition, feature, cancellationToken);

            _performanceMonitor.RecordDatabaseQuery("update", layerDefinition.Id.ToString(), stopwatch.Elapsed, 1);

            MonitoredFeatureStoreLog.UpdateCompleted(_logger,
                layerDefinition.Id.ToString(),
                stopwatch.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.UpdateFailed(_logger, layerDefinition.Id.ToString(), ex.Message, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        LayerDefinition layerDefinition,
        object id,
        CancellationToken cancellationToken = default)
    {
        using var scope = _performanceMonitor.StartOperation("delete_feature")
            .WithTag("layer_id", layerDefinition.Id.ToString())
            .WithTag("operation", "delete");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _innerStore.DeleteAsync(layerDefinition, id, cancellationToken);

            _performanceMonitor.RecordDatabaseQuery("delete", layerDefinition.Id.ToString(), stopwatch.Elapsed, result ? 1 : 0);

            MonitoredFeatureStoreLog.DeleteCompleted(_logger,
                layerDefinition.Id.ToString(),
                id.ToString()!,
                result,
                stopwatch.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.DeleteFailed(_logger, layerDefinition.Id.ToString(), id.ToString()!, ex.Message, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> GetMvtTileAsync(
        LayerDefinition layerDefinition,
        TileCoordinate coordinate,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        using var scope = _performanceMonitor.StartOperation("get_mvt_tile")
            .WithTag("layer_id", layerDefinition.Id.ToString())
            .WithTag("operation", "mvt_tile")
            .WithTag("tile_z", coordinate.Z.ToString())
            .WithTag("tile_x", coordinate.X.ToString())
            .WithTag("tile_y", coordinate.Y.ToString());

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _innerStore.GetMvtTileAsync(layerDefinition, coordinate, query, cancellationToken);

            _performanceMonitor.RecordDatabaseQuery("mvt_tile", layerDefinition.Id.ToString(), stopwatch.Elapsed, 1);

            MonitoredFeatureStoreLog.MvtTileCompleted(_logger,
                layerDefinition.Id.ToString(),
                coordinate.Z, coordinate.X, coordinate.Y,
                result.Length,
                stopwatch.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.MvtTileFailed(_logger,
                layerDefinition.Id.ToString(),
                coordinate.Z, coordinate.X, coordinate.Y,
                ex.Message, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<Envelope?> GetExtentAsync(
        LayerDefinition layerDefinition,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        using var scope = _performanceMonitor.StartOperation("get_extent")
            .WithTag("layer_id", layerDefinition.Id.ToString())
            .WithTag("operation", "extent");

        return _innerStore.GetExtentAsync(layerDefinition, query, cancellationToken);
    }

    /// <summary>
    /// Monitors streaming results to count features for metrics.
    /// </summary>
    private async IAsyncEnumerable<Feature> MonitorResults(
        IAsyncEnumerable<Feature> features,
        string queryType,
        string layerId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var count = 0;
        var stopwatch = Stopwatch.StartNew();

        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            count++;
            yield return feature;
        }

        stopwatch.Stop();

        // Record the final metrics
        _performanceMonitor.RecordDatabaseQuery(queryType, layerId, stopwatch.Elapsed, count);

        MonitoredFeatureStoreLog.StreamingQueryCompleted(_logger, layerId, queryType, count, stopwatch.Elapsed.TotalMilliseconds);
    }
}