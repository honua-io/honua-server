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
using Honua.Core.Models;
using Honua.Core.Transport.Clients;
using Honua.Core.Transport.Converters;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Mobile.Sdk.Clients;
using DomainFeature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Mobile.Sdk.Storage;

/// <summary>
/// Interface for offline storage services in mobile environments.
/// Provides GeoPackage-based local storage with sync capabilities.
/// </summary>
public interface IOfflineStorageService : IDisposable
{
    /// <summary>
    /// Queries features from offline storage.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="query">Feature query parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Query results from offline storage</returns>
    Task<QueryResult<DomainFeature>> QueryFeaturesAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams features from offline storage as pages.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="query">Feature query parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of feature pages</returns>
    IAsyncEnumerable<FeaturePage> QueryFeaturesStreamAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Caches features for offline use.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="features">Features to cache</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the operation</returns>
    Task CacheFeaturesAsync(
        string serviceId,
        int layerId,
        ImmutableArray<DomainFeature> features,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues edit operations for later synchronization.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="edits">Edit operations to queue</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Edit results with local IDs</returns>
    Task<EditResult> QueueEditsAsync(
        string serviceId,
        int layerId,
        FeatureEdits edits,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks edit operations as synced with the server.
    /// </summary>
    /// <param name="objectIds">Object IDs that were successfully synced</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the operation</returns>
    Task MarkEditsSyncedAsync(
        IEnumerable<long> objectIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronizes pending edit operations with the server.
    /// </summary>
    /// <param name="context">Mobile context for the sync operation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Sync results</returns>
    Task<SyncResult> SyncPendingEditsAsync(
        MobileContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads an area for offline use.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="boundingBox">Area to download</param>
    /// <param name="context">Mobile context with progress reporting</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Download results</returns>
    Task<DownloadResult> DownloadAreaAsync(
        string serviceId,
        int layerId,
        Envelope boundingBox,
        MobileContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if cached data exists for the specified service and layer.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if cached data exists</returns>
    Task<bool> HasCachedDataAsync(
        string serviceId,
        int layerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets storage statistics for monitoring and cleanup.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Storage statistics</returns>
    Task<StorageStatistics> GetStorageStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleans up old cached data based on retention policies.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Cleanup results</returns>
    Task<CleanupResult> CleanupOldDataAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Results from synchronizing offline edits.
/// </summary>
public class SyncResult
{
    /// <summary>
    /// Number of operations successfully synced.
    /// </summary>
    public int SyncedOperations { get; set; }

    /// <summary>
    /// Number of operations that failed to sync.
    /// </summary>
    public int FailedOperations { get; set; }

    /// <summary>
    /// Errors that occurred during sync.
    /// </summary>
    public IList<SyncError> Errors { get; set; } = new List<SyncError>();

    /// <summary>
    /// Whether the sync completed successfully.
    /// </summary>
    public bool IsSuccess => FailedOperations == 0;
}

/// <summary>
/// Error information from sync operations.
/// </summary>
public class SyncError
{
    /// <summary>
    /// Object ID that failed to sync.
    /// </summary>
    public long ObjectId { get; set; }

    /// <summary>
    /// Type of operation that failed.
    /// </summary>
    public string OperationType { get; set; } = string.Empty;

    /// <summary>
    /// Error message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Exception details if available.
    /// </summary>
    public Exception? Exception { get; set; }
}

/// <summary>
/// Results from downloading an area for offline use.
/// </summary>
public class DownloadResult
{
    /// <summary>
    /// Number of features downloaded.
    /// </summary>
    public int FeaturesDownloaded { get; set; }

    /// <summary>
    /// Size of downloaded data in bytes.
    /// </summary>
    public long DataSizeBytes { get; set; }

    /// <summary>
    /// Time taken to download.
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Whether the download completed successfully.
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// Error message if download failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Statistics about offline storage usage.
/// </summary>
public class StorageStatistics
{
    /// <summary>
    /// Total database size in bytes.
    /// </summary>
    public long DatabaseSizeBytes { get; set; }

    /// <summary>
    /// Number of cached features.
    /// </summary>
    public int CachedFeatureCount { get; set; }

    /// <summary>
    /// Number of pending edit operations.
    /// </summary>
    public int PendingEditCount { get; set; }

    /// <summary>
    /// Oldest cached data timestamp.
    /// </summary>
    public DateTime? OldestCacheDate { get; set; }

    /// <summary>
    /// Available storage space in bytes.
    /// </summary>
    public long AvailableSpaceBytes { get; set; }
}

/// <summary>
/// Results from cleaning up old cached data.
/// </summary>
public class CleanupResult
{
    /// <summary>
    /// Number of features removed.
    /// </summary>
    public int FeaturesRemoved { get; set; }

    /// <summary>
    /// Amount of space freed in bytes.
    /// </summary>
    public long SpaceFreedBytes { get; set; }

    /// <summary>
    /// Whether cleanup completed successfully.
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// Error message if cleanup failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
}