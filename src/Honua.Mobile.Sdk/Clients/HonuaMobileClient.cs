// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Honua.Core.Models;
using Honua.Core.Transport.Clients;
using Honua.Core.Transport.Converters;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Mobile.Sdk.Storage;
using DomainFeature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Mobile.Sdk.Clients;

/// <summary>
/// Mobile-optimized client for Honua feature services.
/// Provides offline-first architecture with battery-aware networking and progress reporting.
/// </summary>
public class HonuaMobileClient : IFeatureServiceClient<MobileContext>, IDisposable
{
    private readonly IFeatureServiceClient<MobileContext> _networkClient;
    private readonly IOfflineStorageService _offlineStorage;
    private readonly IConnectivityService _connectivity;
    private readonly ILogger<HonuaMobileClient> _logger;
    private readonly HonuaMobileClientOptions _options;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the HonuaMobileClient.
    /// </summary>
    /// <param name="networkClient">Network client for server communication</param>
    /// <param name="offlineStorage">Offline storage service</param>
    /// <param name="connectivity">Connectivity monitoring service</param>
    /// <param name="options">Client configuration options</param>
    /// <param name="logger">Logger instance</param>
    public HonuaMobileClient(
        IFeatureServiceClient<MobileContext> networkClient,
        IOfflineStorageService offlineStorage,
        IConnectivityService connectivity,
        IOptions<HonuaMobileClientOptions> options,
        ILogger<HonuaMobileClient> logger)
    {
        _networkClient = networkClient ?? throw new ArgumentNullException(nameof(networkClient));
        _offlineStorage = offlineStorage ?? throw new ArgumentNullException(nameof(offlineStorage));
        _connectivity = connectivity ?? throw new ArgumentNullException(nameof(connectivity));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes a feature query with offline-first behavior.
    /// First attempts to use cached data, then fallback to network if allowed.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="query">Feature query parameters</param>
    /// <param name="context">Mobile context with network policy and progress reporting</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Query results with features</returns>
    public async Task<QueryResult<DomainFeature>> QueryFeaturesAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        MobileContext context,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, cancellationToken);

        var token = combinedCts.Token;

        try
        {
            context.ProgressReporter?.Report(SyncProgress.Step("Query", 0, 1, "Starting query..."));
            var offlineQuerySupported = !FeatureQueryOfflineSupport.TryGetUnsupportedReason(query, out var unsupportedOfflineReason);

            // Try offline first if allowed
            if (context.AllowOffline && offlineQuerySupported)
            {
                _logger.LogDebug("Attempting offline query for service {ServiceId}, layer {LayerId}",
                    serviceId, layerId);

                var offlineResult = await _offlineStorage.QueryFeaturesAsync(
                    serviceId, layerId, query, token);

                if (offlineResult.Features.Any())
                {
                    context.ProgressReporter?.Report(SyncProgress.Completed("Query completed from offline cache"));
                    _logger.LogDebug("Offline query returned {FeatureCount} features", offlineResult.Features.Length);
                    return offlineResult;
                }

                _logger.LogDebug("No offline data available, attempting network query");
            }
            else if (context.AllowOffline && unsupportedOfflineReason is not null)
            {
                _logger.LogInformation(
                    "Skipping offline query for service {ServiceId}, layer {LayerId}: {Reason}",
                    serviceId,
                    layerId,
                    unsupportedOfflineReason);
            }

            // Check network policy and connectivity
            if (context.NetworkPolicy == NetworkPolicy.Offline)
            {
                if (!offlineQuerySupported)
                {
                    throw new NotSupportedException(unsupportedOfflineReason);
                }

                context.ProgressReporter?.Report(SyncProgress.Completed("Query completed (offline only)"));
                return new QueryResult<DomainFeature>
                {
                    Features = ImmutableArray<DomainFeature>.Empty
                };
            }

            if (!await _connectivity.IsConnectionAvailableAsync(context.NetworkPolicy))
            {
                context.ProgressReporter?.Report(SyncProgress.Failed(
                    new InvalidOperationException("No network connection available"),
                    "Query failed - no network connection"));

                if (context.AllowOffline)
                {
                    if (!offlineQuerySupported)
                    {
                        throw new NotSupportedException(unsupportedOfflineReason);
                    }

                    _logger.LogWarning("Network unavailable, returning empty offline result");
                    return new QueryResult<DomainFeature>
                    {
                        Features = ImmutableArray<DomainFeature>.Empty
                    };
                }

                throw new InvalidOperationException("No network connection available and offline mode disabled");
            }

            // Check battery policy
            if (!await _connectivity.IsBatteryLevelSufficientAsync(context.BatteryPolicy))
            {
                _logger.LogWarning("Battery level too low for network operation with policy {Policy}",
                    context.BatteryPolicy);

                context.ProgressReporter?.Report(SyncProgress.Failed(
                    new InvalidOperationException("Battery level too low"),
                    "Query deferred due to low battery"));

                throw new InvalidOperationException("Battery level too low for network operation");
            }

            context.ProgressReporter?.Report(SyncProgress.Step("Query", 0, 1, "Querying server..."));

            // Execute network query
            var networkResult = await _networkClient.QueryFeaturesAsync(
                serviceId, layerId, query, context, token);

            // Cache results for offline use
            if (networkResult.Features.Any() && context.AllowOffline)
            {
                await _offlineStorage.CacheFeaturesAsync(
                    serviceId, layerId, networkResult.Features, token);
            }

            context.ProgressReporter?.Report(SyncProgress.Completed($"Query completed with {networkResult.Features.Length} features"));

            _logger.LogDebug("Network query completed with {FeatureCount} features",
                networkResult.Features.Length);

            return networkResult;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            context.ProgressReporter?.Report(SyncProgress.Failed(
                new OperationCanceledException("Query was cancelled"),
                "Query cancelled"));
            _logger.LogDebug("Query was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            context.ProgressReporter?.Report(SyncProgress.Failed(ex, $"Query failed: {ex.Message}"));
            _logger.LogError(ex, "Query failed for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);
            throw;
        }
    }

    /// <summary>
    /// Executes a feature query and streams results as pages with mobile-optimized buffering.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="query">Feature query parameters</param>
    /// <param name="context">Mobile context with progress reporting</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of feature pages</returns>
    public async IAsyncEnumerable<FeaturePage> QueryFeaturesStreamAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        MobileContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, cancellationToken);

        var token = combinedCts.Token;

        // For mobile streaming, prefer smaller page sizes to conserve memory
        var adjustedQuery = query with
        {
            ResultRecordCount = Math.Min(query.ResultRecordCount ?? 500, _options.MobilePageSize)
        };

        context.ProgressReporter?.Report(SyncProgress.Step("Stream", 0, 1, "Starting streaming query..."));

        var pageCount = 0;
        var totalFeatures = 0;
        var offlineQuerySupported = !FeatureQueryOfflineSupport.TryGetUnsupportedReason(adjustedQuery, out var unsupportedOfflineReason);

        if (context.AllowOffline && offlineQuerySupported)
        {
            var hasOfflineData = await _offlineStorage.HasCachedDataAsync(serviceId, layerId, token);
            if (hasOfflineData)
            {
                await foreach (var offlinePage in _offlineStorage.QueryFeaturesStreamAsync(
                    serviceId, layerId, adjustedQuery, token))
                {
                    pageCount++;
                    totalFeatures += offlinePage.Features.Length;

                    context.ProgressReporter?.Report(SyncProgress.Step("Stream",
                        totalFeatures, totalFeatures + 100,
                        $"Page {pageCount} from offline cache ({offlinePage.Features.Length} features)"));

                    yield return offlinePage;

                    if (offlinePage.IsLastPage)
                    {
                        context.ProgressReporter?.Report(SyncProgress.Completed(
                            $"Streaming completed from offline cache - {totalFeatures} features"));
                        yield break;
                    }
                }
            }
        }
        else if (context.AllowOffline && unsupportedOfflineReason is not null)
        {
            _logger.LogInformation(
                "Skipping offline streaming query for service {ServiceId}, layer {LayerId}: {Reason}",
                serviceId,
                layerId,
                unsupportedOfflineReason);
        }

        if (context.NetworkPolicy == NetworkPolicy.Offline && !offlineQuerySupported)
        {
            throw new NotSupportedException(unsupportedOfflineReason);
        }

        if (context.NetworkPolicy != NetworkPolicy.Offline &&
            await _connectivity.IsConnectionAvailableAsync(context.NetworkPolicy) &&
            await _connectivity.IsBatteryLevelSufficientAsync(context.BatteryPolicy))
        {
            pageCount = 0;
            totalFeatures = 0;

            await foreach (var networkPage in _networkClient.QueryFeaturesStreamAsync(
                serviceId, layerId, adjustedQuery, context, token))
            {
                pageCount++;
                totalFeatures += networkPage.Features.Length;

                if (context.AllowOffline && networkPage.Features.Any())
                {
                    await _offlineStorage.CacheFeaturesAsync(
                        serviceId, layerId, networkPage.Features, token);
                }

                context.ProgressReporter?.Report(SyncProgress.Step("Stream",
                    totalFeatures, totalFeatures + 100,
                    $"Page {pageCount} from server ({networkPage.Features.Length} features)"));

                yield return networkPage;

                if (networkPage.IsLastPage)
                {
                    context.ProgressReporter?.Report(SyncProgress.Completed(
                        $"Streaming completed from server - {totalFeatures} features"));
                    break;
                }

                if (pageCount % 5 == 0)
                {
                    await Task.Delay(50, token);
                }
            }
        }
        else if (!offlineQuerySupported)
        {
            throw new NotSupportedException(unsupportedOfflineReason);
        }
    }

    /// <summary>
    /// Applies feature edits with offline queueing for later synchronization.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="edits">Edit operations to apply</param>
    /// <param name="context">Mobile context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Edit results</returns>
    public async Task<EditResult> ApplyEditsAsync(
        string serviceId,
        int layerId,
        FeatureEdits edits,
        MobileContext context,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, cancellationToken);

        var token = combinedCts.Token;

        try
        {
            var totalOperations = edits.Adds.Length + edits.Updates.Length + edits.Deletes.Length;
            context.ProgressReporter?.Report(SyncProgress.Step("Edits", 0, totalOperations,
                $"Applying {totalOperations} edit operations..."));

            // Always queue edits offline first for mobile reliability
            var offlineResult = await _offlineStorage.QueueEditsAsync(
                serviceId, layerId, edits, token);

            // Try immediate sync if network is available and policy allows
            if (context.NetworkPolicy != NetworkPolicy.Offline &&
                await _connectivity.IsConnectionAvailableAsync(context.NetworkPolicy) &&
                await _connectivity.IsBatteryLevelSufficientAsync(context.BatteryPolicy))
            {
                context.ProgressReporter?.Report(SyncProgress.Step("Edits", totalOperations / 2, totalOperations,
                    "Syncing to server..."));

                try
                {
                    var networkResult = await _networkClient.ApplyEditsAsync(
                        serviceId, layerId, edits, context, token);

                    // Mark as synced if successful
                    await _offlineStorage.MarkEditsSyncedAsync(GetPendingEditIds(offlineResult), token);

                    context.ProgressReporter?.Report(SyncProgress.Completed(
                        $"Edits applied and synced - {totalOperations} operations"));

                    return networkResult;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to sync edits immediately, will retry later");
                    context.ProgressReporter?.Report(SyncProgress.Step("Edits", totalOperations, totalOperations,
                        "Edits saved offline, will sync when network improves"));
                }
            }
            else
            {
                context.ProgressReporter?.Report(SyncProgress.Completed(
                    $"Edits saved offline - {totalOperations} operations queued for sync"));
            }

            _logger.LogDebug("Edits queued offline for service {ServiceId}, layer {LayerId}: " +
                           "Adds: {AddCount}, Updates: {UpdateCount}, Deletes: {DeleteCount}",
                serviceId, layerId, edits.Adds.Length, edits.Updates.Length, edits.Deletes.Length);

            return offlineResult;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            context.ProgressReporter?.Report(SyncProgress.Failed(
                new OperationCanceledException("Edit operation was cancelled"),
                "Edits cancelled"));
            throw;
        }
        catch (Exception ex)
        {
            context.ProgressReporter?.Report(SyncProgress.Failed(ex, $"Edit operation failed: {ex.Message}"));
            _logger.LogError(ex, "Edit operation failed for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);
            throw;
        }
    }

    /// <summary>
    /// Convenience method for querying features with just a cancellation token.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="query">Feature query parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Query results with features</returns>
    public Task<QueryResult<DomainFeature>> QueryFeaturesAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        var context = new MobileContext { CancellationToken = cancellationToken };
        return QueryFeaturesAsync(serviceId, layerId, query, context, cancellationToken);
    }

    /// <summary>
    /// Synchronizes pending offline edits with the server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Sync results</returns>
    public async Task<SyncResult> SyncPendingEditsAsync(CancellationToken cancellationToken = default)
    {
        var context = MobileContext.Background(cancellationToken);
        return await _offlineStorage.SyncPendingEditsAsync(context, cancellationToken);
    }

    /// <summary>
    /// Downloads an area for offline use.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="boundingBox">Area to download</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Download results</returns>
    public async Task<DownloadResult> DownloadAreaAsync(
        string serviceId,
        int layerId,
        Envelope boundingBox,
        CancellationToken cancellationToken = default)
    {
        var context = MobileContext.WithProgress(
            new Progress<SyncProgress>(p => _logger.LogDebug("Download progress: {Message}", p.Message)),
            cancellationToken);

        return await _offlineStorage.DownloadAreaAsync(serviceId, layerId, boundingBox, context, cancellationToken);
    }

    /// <summary>
    /// Disposes the client and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _offlineStorage?.Dispose();
            if (_networkClient is IDisposable disposableClient)
            {
                disposableClient.Dispose();
            }
            _disposed = true;
        }
    }

    private static IEnumerable<long> GetPendingEditIds(EditResult editResult)
    {
        return editResult.AddResults
            .Concat(editResult.UpdateResults)
            .Concat(editResult.DeleteResults)
            .Select(result => result.ObjectId);
    }
}
