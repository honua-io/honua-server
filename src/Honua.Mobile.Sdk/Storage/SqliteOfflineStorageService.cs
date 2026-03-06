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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Honua.Core.Models;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Transport.Clients;
using Honua.Mobile.Sdk.Clients;
using DomainFeature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Mobile.Sdk.Storage;

/// <summary>
/// SQLite-based offline storage implementation for mobile environments.
/// Uses Entity Framework Core with real domain models from Honua.Core.Sdk.
/// </summary>
public class SqliteOfflineStorageService : IOfflineStorageService
{
    private readonly ILogger<SqliteOfflineStorageService> _logger;
    private readonly HonuaMobileClientOptions _options;
    private readonly OfflineDbContext _dbContext;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the SqliteOfflineStorageService.
    /// </summary>
    /// <param name="dbContext">Entity Framework database context</param>
    /// <param name="options">Mobile client options</param>
    /// <param name="logger">Logger instance</param>
    public SqliteOfflineStorageService(
        OfflineDbContext dbContext,
        IOptions<HonuaMobileClientOptions> options,
        ILogger<SqliteOfflineStorageService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Queries features from offline storage.
    /// </summary>
    public async Task<QueryResult<DomainFeature>> QueryFeaturesAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIfDisposed(_disposed, this);

        try
        {
            _logger.LogDebug("Querying offline features for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);

            var cachedFeatures = await _dbContext.CachedFeatures
                .Where(cf => cf.ServiceId == serviceId && cf.LayerId == layerId)
                .ApplyFeatureQuery(query)
                .ToListAsync(cancellationToken);

            var features = cachedFeatures.Select(cf => cf.ToDomainFeature()).ToImmutableArray();

            _logger.LogDebug("Found {FeatureCount} features in offline storage", features.Length);

            return new QueryResult<DomainFeature>
            {
                Features = features,
                ExceededTransferLimit = false
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying offline features for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);
            throw;
        }
    }

    /// <summary>
    /// Streams features from offline storage as pages.
    /// </summary>
    public async IAsyncEnumerable<FeaturePage> QueryFeaturesStreamAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIfDisposed(_disposed, this);

        try
        {
            _logger.LogDebug("Starting offline streaming query for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);

            var pageSize = query.ResultRecordCount ?? _options.MobilePageSize;
            var currentOffset = query.ResultOffset ?? 0;
            var pageNumber = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pageQuery = query with
                {
                    ResultOffset = currentOffset,
                    ResultRecordCount = pageSize
                };

                var cachedFeatures = await _dbContext.CachedFeatures
                    .Where(cf => cf.ServiceId == serviceId && cf.LayerId == layerId)
                    .ApplyFeatureQuery(pageQuery)
                    .ToListAsync(cancellationToken);

                if (!cachedFeatures.Any())
                {
                    if (pageNumber == 0)
                    {
                        // Return empty page for first page
                        yield return new FeaturePage
                        {
                            Features = ImmutableArray<DomainFeature>.Empty,
                            IsLastPage = true,
                            PageNumber = 0
                        };
                    }
                    break;
                }

                var features = cachedFeatures.Select(cf => cf.ToDomainFeature()).ToImmutableArray();
                var isLastPage = cachedFeatures.Count < pageSize;

                _logger.LogDebug("Streaming offline page {PageNumber} with {FeatureCount} features",
                    pageNumber, features.Length);

                yield return new FeaturePage
                {
                    Features = features,
                    IsLastPage = isLastPage,
                    PageNumber = pageNumber
                };

                if (isLastPage) break;

                currentOffset += pageSize;
                pageNumber++;
            }

            _logger.LogDebug("Offline streaming completed after {PageCount} pages", pageNumber + 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming offline features for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);
            throw;
        }
    }

    /// <summary>
    /// Caches features for offline use.
    /// </summary>
    public async Task CacheFeaturesAsync(
        string serviceId,
        int layerId,
        ImmutableArray<DomainFeature> features,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIfDisposed(_disposed, this);

        try
        {
            _logger.LogDebug("Caching {FeatureCount} features for service {ServiceId}, layer {LayerId}",
                features.Length, serviceId, layerId);

            // Convert domain features to cached entities
            var cachedFeatures = features.Select(feature => CachedFeatureEntity.FromDomainFeature(
                feature, serviceId, layerId)).ToList();

            // Use upsert logic to handle duplicates
            foreach (var cachedFeature in cachedFeatures)
            {
                var existing = await _dbContext.CachedFeatures
                    .FirstOrDefaultAsync(cf =>
                        cf.ServiceId == serviceId &&
                        cf.LayerId == layerId &&
                        cf.ObjectId == cachedFeature.ObjectId,
                        cancellationToken);

                if (existing != null)
                {
                    // Update existing
                    existing.UpdateFromDomainFeature(cachedFeature);
                    _dbContext.CachedFeatures.Update(existing);
                }
                else
                {
                    // Add new
                    _dbContext.CachedFeatures.Add(cachedFeature);
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogDebug("Successfully cached {FeatureCount} features", features.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error caching features for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);
            throw;
        }
    }

    /// <summary>
    /// Queues edit operations for later synchronization.
    /// </summary>
    public async Task<EditResult> QueueEditsAsync(
        string serviceId,
        int layerId,
        FeatureEdits edits,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIfDisposed(_disposed, this);

        try
        {
            var totalOperations = edits.Adds.Length + edits.Updates.Length + edits.Deletes.Length;
            _logger.LogDebug("Queueing {TotalOperations} edit operations for service {ServiceId}, layer {LayerId}",
                totalOperations, serviceId, layerId);

            using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            var addResults = new List<EditResultRecord>();
            var updateResults = new List<EditResultRecord>();
            var deleteResults = new List<EditResultRecord>();

            // Process adds
            foreach (var feature in edits.Adds)
            {
                var pendingEdit = new PendingEditEntity
                {
                    ServiceId = serviceId,
                    LayerId = layerId,
                    OperationType = "Add",
                    FeatureData = SerializeFeature(feature),
                    CreatedAt = DateTime.UtcNow,
                    IsSynced = false
                };

                _dbContext.PendingEdits.Add(pendingEdit);
                await _dbContext.SaveChangesAsync(cancellationToken);

                addResults.Add(new EditResultRecord
                {
                    ObjectId = pendingEdit.Id, // Use local ID
                    IsSuccess = true
                });
            }

            // Process updates
            foreach (var feature in edits.Updates)
            {
                var pendingEdit = new PendingEditEntity
                {
                    ServiceId = serviceId,
                    LayerId = layerId,
                    OperationType = "Update",
                    OriginalObjectId = feature.ObjectId,
                    FeatureData = SerializeFeature(feature),
                    CreatedAt = DateTime.UtcNow,
                    IsSynced = false
                };

                _dbContext.PendingEdits.Add(pendingEdit);

                updateResults.Add(new EditResultRecord
                {
                    ObjectId = feature.ObjectId ?? 0,
                    IsSuccess = true
                });
            }

            // Process deletes
            foreach (var objectId in edits.Deletes)
            {
                var pendingEdit = new PendingEditEntity
                {
                    ServiceId = serviceId,
                    LayerId = layerId,
                    OperationType = "Delete",
                    OriginalObjectId = objectId,
                    CreatedAt = DateTime.UtcNow,
                    IsSynced = false
                };

                _dbContext.PendingEdits.Add(pendingEdit);

                deleteResults.Add(new EditResultRecord
                {
                    ObjectId = objectId,
                    IsSuccess = true
                });
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogDebug("Successfully queued {TotalOperations} edit operations", totalOperations);

            return new EditResult
            {
                AddResults = addResults.ToImmutableArray(),
                UpdateResults = updateResults.ToImmutableArray(),
                DeleteResults = deleteResults.ToImmutableArray()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error queueing edits for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);
            throw;
        }
    }

    /// <summary>
    /// Marks edit operations as synced with the server.
    /// </summary>
    public async Task MarkEditsSyncedAsync(
        IEnumerable<long> objectIds,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIfDisposed(_disposed, this);

        try
        {
            var objectIdList = objectIds.ToList();
            _logger.LogDebug("Marking {EditCount} edits as synced", objectIdList.Count);

            var pendingEdits = await _dbContext.PendingEdits
                .Where(pe => objectIdList.Contains(pe.Id))
                .ToListAsync(cancellationToken);

            foreach (var edit in pendingEdits)
            {
                edit.IsSynced = true;
                edit.SyncedAt = DateTime.UtcNow;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogDebug("Successfully marked {EditCount} edits as synced", pendingEdits.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking edits as synced");
            throw;
        }
    }

    /// <summary>
    /// Synchronizes pending edit operations with the server.
    /// </summary>
    public async Task<SyncResult> SyncPendingEditsAsync(
        MobileContext context,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIfDisposed(_disposed, this);

        // This is a placeholder implementation
        // Real implementation would need a network client to sync with server
        _logger.LogInformation("Sync pending edits not yet implemented - requires network client integration");

        await Task.CompletedTask;

        return new SyncResult
        {
            SyncedOperations = 0,
            FailedOperations = 0,
            Errors = new List<SyncError>()
        };
    }

    /// <summary>
    /// Downloads an area for offline use.
    /// </summary>
    public async Task<DownloadResult> DownloadAreaAsync(
        string serviceId,
        int layerId,
        Envelope boundingBox,
        MobileContext context,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIfDisposed(_disposed, this);

        // This is a placeholder implementation
        // Real implementation would need a network client to download from server
        _logger.LogInformation("Download area not yet implemented - requires network client integration");

        await Task.CompletedTask;

        return new DownloadResult
        {
            FeaturesDownloaded = 0,
            DataSizeBytes = 0,
            Duration = TimeSpan.Zero,
            IsSuccess = false,
            ErrorMessage = "Download not yet implemented"
        };
    }

    /// <summary>
    /// Checks if cached data exists for the specified service and layer.
    /// </summary>
    public async Task<bool> HasCachedDataAsync(
        string serviceId,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIfDisposed(_disposed, this);

        try
        {
            return await _dbContext.CachedFeatures
                .AnyAsync(cf => cf.ServiceId == serviceId && cf.LayerId == layerId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking cached data for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);
            return false;
        }
    }

    /// <summary>
    /// Gets storage statistics for monitoring and cleanup.
    /// </summary>
    public async Task<StorageStatistics> GetStorageStatisticsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIfDisposed(_disposed, this);

        try
        {
            var featureCount = await _dbContext.CachedFeatures.CountAsync(cancellationToken);
            var pendingEditCount = await _dbContext.PendingEdits
                .CountAsync(pe => !pe.IsSynced, cancellationToken);

            var oldestCacheDate = await _dbContext.CachedFeatures
                .MinAsync(cf => (DateTime?)cf.CachedAt, cancellationToken);

            // Note: Getting actual database size would require platform-specific code
            var databaseSize = 0L; // Placeholder

            return new StorageStatistics
            {
                DatabaseSizeBytes = databaseSize,
                CachedFeatureCount = featureCount,
                PendingEditCount = pendingEditCount,
                OldestCacheDate = oldestCacheDate,
                AvailableSpaceBytes = long.MaxValue // Placeholder
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting storage statistics");
            throw;
        }
    }

    /// <summary>
    /// Cleans up old cached data based on retention policies.
    /// </summary>
    public async Task<CleanupResult> CleanupOldDataAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIfDisposed(_disposed, this);

        try
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-_options.OfflineRetentionDays);

            var oldFeatures = await _dbContext.CachedFeatures
                .Where(cf => cf.CachedAt < cutoffDate)
                .ToListAsync(cancellationToken);

            var featuresRemoved = oldFeatures.Count;

            if (featuresRemoved > 0)
            {
                _dbContext.CachedFeatures.RemoveRange(oldFeatures);
                await _dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Cleaned up {FeaturesRemoved} old cached features", featuresRemoved);
            }

            return new CleanupResult
            {
                FeaturesRemoved = featuresRemoved,
                SpaceFreedBytes = featuresRemoved * 1024, // Rough estimate
                IsSuccess = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up old data");
            return new CleanupResult
            {
                FeaturesRemoved = 0,
                SpaceFreedBytes = 0,
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Serializes a feature for storage.
    /// </summary>
    private static string SerializeFeature(DomainFeature feature)
    {
        // Simplified serialization - in production use proper JSON serialization
        // with geometry handling
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            feature.ObjectId,
            feature.Attributes,
            GeometryWkt = feature.Geometry?.ToString() // Convert to WKT
        });
    }

    /// <summary>
    /// Disposes the service and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _dbContext?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Entity Framework query extensions for FeatureQuery.
/// </summary>
public static class FeatureQueryExtensions
{
    /// <summary>
    /// Applies feature query parameters to a queryable.
    /// </summary>
    public static IQueryable<CachedFeatureEntity> ApplyFeatureQuery(
        this IQueryable<CachedFeatureEntity> query, FeatureQuery featureQuery)
    {
        // Apply where clause
        if (!string.IsNullOrEmpty(featureQuery.Where))
        {
            // Simplified where clause handling - in production would need proper SQL generation
            // from the where clause expression
        }

        // Apply spatial filter
        if (featureQuery.SpatialFilter != null)
        {
            // Simplified spatial filtering - in production would use spatial database functions
            // This is a placeholder for proper spatial query implementation
        }

        // Apply ordering
        query = query.OrderBy(cf => cf.ObjectId);

        // Apply offset and limit
        if (featureQuery.ResultOffset.HasValue)
        {
            query = query.Skip(featureQuery.ResultOffset.Value);
        }

        if (featureQuery.ResultRecordCount.HasValue)
        {
            query = query.Take(featureQuery.ResultRecordCount.Value);
        }

        return query;
    }
}