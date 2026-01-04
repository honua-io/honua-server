// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Tiles;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.FeatureStore;

/// <summary>
/// Decorator for IFeatureStore that adds comprehensive performance monitoring and telemetry.
/// </summary>
/// <remarks>
/// This decorator wraps any IFeatureStore implementation to provide detailed performance metrics
/// including query timing, record counts, cache metrics, and database operation tracking.
/// </remarks>
internal sealed class MonitoredFeatureStoreDecorator : IFeatureStore, IStreamingFeatureStore, IGmlFeatureStore
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
    public async Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);
        var featureIdText = featureId.ToString(CultureInfo.InvariantCulture);

        using var scope = _performanceMonitor.StartOperation("get_feature")
            .WithTag("layer_id", layerIdText)
            .WithTag("operation", "get");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _innerStore.GetAsync(layerId, featureId, cancellationToken);

            _performanceMonitor.RecordDatabaseQuery("get", layerIdText, stopwatch.Elapsed, result != null ? 1 : 0);

            MonitoredFeatureStoreLog.GetCompleted(
                _logger,
                layerIdText,
                featureIdText,
                result != null,
                stopwatch.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.GetFailed(_logger, layerIdText, featureIdText, ex.Message, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);

        using var scope = _performanceMonitor.StartOperation("query_features")
            .WithTag("layer_id", layerIdText)
            .WithTag("operation", "query");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _innerStore.QueryAsync(layerId, query, cancellationToken);
            var itemCount = result.Items.Length;

            _performanceMonitor.RecordDatabaseQuery("query", layerIdText, stopwatch.Elapsed, itemCount);

            MonitoredFeatureStoreLog.QueryCompleted(_logger, layerIdText, stopwatch.Elapsed.TotalMilliseconds);
            MonitoredFeatureStoreLog.StreamingQueryCompleted(_logger, layerIdText, "query", itemCount, stopwatch.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.QueryFailed(_logger, layerIdText, ex.Message, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<QueryResult<GmlFeature>> QueryGmlAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);

        using var scope = _performanceMonitor.StartOperation("query_gml")
            .WithTag("layer_id", layerIdText)
            .WithTag("operation", "query_gml");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await GetGmlStore().QueryGmlAsync(layerId, query, cancellationToken);
            var itemCount = result.Items.Length;

            _performanceMonitor.RecordDatabaseQuery("query_gml", layerIdText, stopwatch.Elapsed, itemCount);

            MonitoredFeatureStoreLog.QueryCompleted(_logger, layerIdText, stopwatch.Elapsed.TotalMilliseconds);
            MonitoredFeatureStoreLog.StreamingQueryCompleted(_logger, layerIdText, "query_gml", itemCount, stopwatch.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.QueryFailed(_logger, layerIdText, ex.Message, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Feature> StreamFeaturesAsync(
        int layerId,
        FeatureQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);

        using var scope = _performanceMonitor.StartOperation("stream_features")
            .WithTag("layer_id", layerIdText)
            .WithTag("operation", "stream");

        var stopwatch = Stopwatch.StartNew();
        var itemCount = 0;
        var completed = false;

        try
        {
            await foreach (var feature in GetStreamingStore().StreamFeaturesAsync(layerId, query, cancellationToken))
            {
                itemCount++;
                yield return feature;
            }

            completed = true;
        }
        finally
        {
            stopwatch.Stop();
            if (completed)
            {
                _performanceMonitor.RecordDatabaseQuery("stream_features", layerIdText, stopwatch.Elapsed, itemCount);
                MonitoredFeatureStoreLog.StreamingQueryCompleted(
                    _logger,
                    layerIdText,
                    "stream",
                    itemCount,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IReadOnlyList<Feature>> StreamFeatureBatchesAsync(
        int layerId,
        FeatureQuery query,
        int batchSize = 1000,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);

        using var scope = _performanceMonitor.StartOperation("stream_feature_batches")
            .WithTag("layer_id", layerIdText)
            .WithTag("operation", "stream_batches");

        var stopwatch = Stopwatch.StartNew();
        var itemCount = 0;
        var completed = false;

        try
        {
            await foreach (var batch in GetStreamingStore()
                               .StreamFeatureBatchesAsync(layerId, query, batchSize, cancellationToken))
            {
                itemCount += batch.Count;
                yield return batch;
            }

            completed = true;
        }
        finally
        {
            stopwatch.Stop();
            if (completed)
            {
                _performanceMonitor.RecordDatabaseQuery("stream_batches", layerIdText, stopwatch.Elapsed, itemCount);
                MonitoredFeatureStoreLog.StreamingQueryCompleted(
                    _logger,
                    layerIdText,
                    "stream_batches",
                    itemCount,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<GmlFeature> StreamGmlFeaturesAsync(
        int layerId,
        FeatureQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);

        using var scope = _performanceMonitor.StartOperation("stream_gml_features")
            .WithTag("layer_id", layerIdText)
            .WithTag("operation", "stream_gml");

        var stopwatch = Stopwatch.StartNew();
        var itemCount = 0;
        var completed = false;

        try
        {
            await foreach (var feature in GetStreamingStore().StreamGmlFeaturesAsync(layerId, query, cancellationToken))
            {
                itemCount++;
                yield return feature;
            }

            completed = true;
        }
        finally
        {
            stopwatch.Stop();
            if (completed)
            {
                _performanceMonitor.RecordDatabaseQuery("stream_gml_features", layerIdText, stopwatch.Elapsed, itemCount);
                MonitoredFeatureStoreLog.StreamingQueryCompleted(
                    _logger,
                    layerIdText,
                    "stream_gml",
                    itemCount,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
        }
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);

        using var scope = _performanceMonitor.StartOperation("count_features")
            .WithTag("layer_id", layerIdText)
            .WithTag("operation", "count");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _innerStore.CountAsync(layerId, query, cancellationToken);

            var recordCount = result > int.MaxValue ? int.MaxValue : (int)result;
            _performanceMonitor.RecordDatabaseQuery("count", layerIdText, stopwatch.Elapsed, recordCount);

            MonitoredFeatureStoreLog.CountCompleted(_logger, layerIdText, result, stopwatch.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.CountFailed(_logger, layerIdText, ex.Message, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
    {
        var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);

        using var scope = _performanceMonitor.StartOperation("get_extent")
            .WithTag("layer_id", layerIdText)
            .WithTag("operation", "extent");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _innerStore.GetExtentAsync(layerId, query, cancellationToken);

            _performanceMonitor.RecordDatabaseQuery("extent", layerIdText, stopwatch.Elapsed, result != null ? 1 : 0);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.QueryFailed(_logger, layerIdText, ex.Message, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetMvtTileAsync(
        int layerId,
        int x,
        int y,
        int z,
        FeatureQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        return await GetMvtTileInternalAsync(layerId, x, y, z, query, tileOptions: null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetMvtTileAsync(
        int layerId,
        int x,
        int y,
        int z,
        FeatureQuery? query,
        TileOptions tileOptions,
        CancellationToken cancellationToken = default)
    {
        return await GetMvtTileInternalAsync(layerId, x, y, z, query, tileOptions, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<QueryResult<Feature>> QueryRelatedAsync(int layerId, RelatedQuery query, CancellationToken cancellationToken = default)
    {
        var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);

        using var scope = _performanceMonitor.StartOperation("query_related")
            .WithTag("layer_id", layerIdText)
            .WithTag("operation", "query_related");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _innerStore.QueryRelatedAsync(layerId, query, cancellationToken);
            var itemCount = result.Items.Length;

            _performanceMonitor.RecordDatabaseQuery("query_related", layerIdText, stopwatch.Elapsed, itemCount);

            MonitoredFeatureStoreLog.StreamingQueryCompleted(_logger, layerIdText, "related", itemCount, stopwatch.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.QueryFailed(_logger, layerIdText, ex.Message, ex);
            throw;
        }
    }

    private IStreamingFeatureStore GetStreamingStore()
    {
        if (_innerStore is IStreamingFeatureStore streamingStore)
        {
            return streamingStore;
        }

        throw new NotSupportedException("Streaming operations are not supported by the configured feature store.");
    }

    private IGmlFeatureStore GetGmlStore()
    {
        if (_innerStore is IGmlFeatureStore gmlStore)
        {
            return gmlStore;
        }

        throw new NotSupportedException("GML query operations are not supported by the configured feature store.");
    }

    /// <inheritdoc />
    public async Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);

        using var scope = _performanceMonitor.StartOperation("create_feature")
            .WithTag("layer_id", layerIdText)
            .WithTag("operation", "create");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _innerStore.CreateAsync(layerId, feature, cancellationToken);

            _performanceMonitor.RecordDatabaseQuery("create", layerIdText, stopwatch.Elapsed, 1);

            MonitoredFeatureStoreLog.CreateCompleted(_logger, layerIdText, stopwatch.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.CreateFailed(_logger, layerIdText, ex.Message, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);

        using var scope = _performanceMonitor.StartOperation("update_feature")
            .WithTag("layer_id", layerIdText)
            .WithTag("operation", "update");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _innerStore.UpdateAsync(layerId, feature, cancellationToken);

            _performanceMonitor.RecordDatabaseQuery("update", layerIdText, stopwatch.Elapsed, 1);

            MonitoredFeatureStoreLog.UpdateCompleted(_logger, layerIdText, stopwatch.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.UpdateFailed(_logger, layerIdText, ex.Message, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);
        var featureIdText = featureId.ToString(CultureInfo.InvariantCulture);

        using var scope = _performanceMonitor.StartOperation("delete_feature")
            .WithTag("layer_id", layerIdText)
            .WithTag("operation", "delete");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _innerStore.DeleteAsync(layerId, featureId, cancellationToken);

            _performanceMonitor.RecordDatabaseQuery("delete", layerIdText, stopwatch.Elapsed, result ? 1 : 0);

            MonitoredFeatureStoreLog.DeleteCompleted(
                _logger,
                layerIdText,
                featureIdText,
                result,
                stopwatch.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.DeleteFailed(_logger, layerIdText, featureIdText, ex.Message, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<FeatureEditResult> ApplyEditsAsync(
        int layerId,
        FeatureEditBatch editBatch,
        CancellationToken cancellationToken = default)
    {
        var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);

        using var scope = _performanceMonitor.StartOperation("apply_edits")
            .WithTag("layer_id", layerIdText)
            .WithTag("operation", "apply_edits");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _innerStore.ApplyEditsAsync(layerId, editBatch, cancellationToken);

            _performanceMonitor.RecordDatabaseQuery("apply_edits", layerIdText, stopwatch.Elapsed, editBatch.TotalOperations);

            MonitoredFeatureStoreLog.ApplyEditsCompleted(
                _logger,
                layerIdText,
                editBatch.Creates.Length,
                editBatch.Updates.Length,
                editBatch.Deletes.Length,
                stopwatch.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.ApplyEditsFailed(_logger, layerIdText, ex.Message, ex);
            throw;
        }
    }

    private async Task<byte[]?> GetMvtTileInternalAsync(
        int layerId,
        int x,
        int y,
        int z,
        FeatureQuery? query,
        TileOptions? tileOptions,
        CancellationToken cancellationToken)
    {
        var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);

        using var scope = _performanceMonitor.StartOperation("get_mvt_tile")
            .WithTag("layer_id", layerIdText)
            .WithTag("operation", "mvt_tile")
            .WithTag("tile_z", z.ToString(CultureInfo.InvariantCulture))
            .WithTag("tile_x", x.ToString(CultureInfo.InvariantCulture))
            .WithTag("tile_y", y.ToString(CultureInfo.InvariantCulture));

        var stopwatch = Stopwatch.StartNew();

        try
        {
            byte[]? result = tileOptions is null
                ? await _innerStore.GetMvtTileAsync(layerId, x, y, z, query, cancellationToken)
                : await _innerStore.GetMvtTileAsync(layerId, x, y, z, query, tileOptions, cancellationToken);

            _performanceMonitor.RecordDatabaseQuery("mvt_tile", layerIdText, stopwatch.Elapsed, result != null ? 1 : 0);

            MonitoredFeatureStoreLog.MvtTileCompleted(
                _logger,
                layerIdText,
                z,
                x,
                y,
                result?.Length ?? 0,
                stopwatch.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            scope.WithTag("error", ex.GetType().Name);
            MonitoredFeatureStoreLog.MvtTileFailed(
                _logger,
                layerIdText,
                z,
                x,
                y,
                ex.Message,
                ex);
            throw;
        }
    }
}
