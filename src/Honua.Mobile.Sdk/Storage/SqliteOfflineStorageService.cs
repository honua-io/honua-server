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
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Honua.Core.Models;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Transport.Clients;
using Honua.Core.Transport.Converters;
using Honua.Mobile.Sdk.Clients;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using DomainFeature = Honua.Core.Features.FeatureStore.Domain.Feature;
using CoreEnvelope = Honua.Core.Models.Envelope;

namespace Honua.Mobile.Sdk.Storage;

/// <summary>
/// SQLite-based offline storage implementation for mobile environments.
/// Uses Entity Framework Core with real domain models from Honua.Core.Sdk.
/// </summary>
public class SqliteOfflineStorageService : IOfflineStorageService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<SqliteOfflineStorageService> _logger;
    private readonly HonuaMobileClientOptions _options;
    private readonly OfflineDbContext _dbContext;
    private readonly IFeatureServiceClient<MobileContext> _networkClient;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the SqliteOfflineStorageService.
    /// </summary>
    /// <param name="dbContext">Entity Framework database context</param>
    /// <param name="options">Mobile client options</param>
    /// <param name="logger">Logger instance</param>
    public SqliteOfflineStorageService(
        OfflineDbContext dbContext,
        IFeatureServiceClient<MobileContext> networkClient,
        IOptions<HonuaMobileClientOptions> options,
        ILogger<SqliteOfflineStorageService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _networkClient = networkClient ?? throw new ArgumentNullException(nameof(networkClient));
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
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            FeatureQueryOfflineSupport.EnsureSupported(query);
            _logger.LogDebug("Querying offline features for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);

            var cachedFeatures = await _dbContext.CachedFeatures
                .Where(cf => cf.ServiceId == serviceId && cf.LayerId == layerId)
                .ApplyFeatureQuery(query)
                .ToListAsync(cancellationToken);

            var features = cachedFeatures
                .Select(cf => FeatureQueryOfflineSupport.ProjectFeature(cf.ToDomainFeature(), query))
                .ToImmutableArray();

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
        ObjectDisposedException.ThrowIf(_disposed, this);
        FeatureQueryOfflineSupport.EnsureSupported(query);

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
                    yield return new FeaturePage
                    {
                        Features = ImmutableArray<DomainFeature>.Empty,
                        IsLastPage = true,
                        PageNumber = 0
                    };
                }

                break;
            }

            var features = cachedFeatures
                .Select(cf => FeatureQueryOfflineSupport.ProjectFeature(cf.ToDomainFeature(), query))
                .ToImmutableArray();
            var isLastPage = cachedFeatures.Count < pageSize;

            _logger.LogDebug("Streaming offline page {PageNumber} with {FeatureCount} features",
                pageNumber, features.Length);

            yield return new FeaturePage
            {
                Features = features,
                IsLastPage = isLastPage,
                PageNumber = pageNumber
            };

            if (isLastPage)
            {
                break;
            }

            currentOffset += pageSize;
            pageNumber++;
        }

        _logger.LogDebug("Offline streaming completed after {PageCount} pages", pageNumber + 1);
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
        ObjectDisposedException.ThrowIf(_disposed, this);

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
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var totalOperations = edits.Adds.Length + edits.Updates.Length + edits.Deletes.Length;
            _logger.LogDebug("Queueing {TotalOperations} edit operations for service {ServiceId}, layer {LayerId}",
                totalOperations, serviceId, layerId);

            using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            var addEdits = new List<PendingEditEntity>();
            var updateEdits = new List<PendingEditEntity>();
            var deleteEdits = new List<PendingEditEntity>();

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
                addEdits.Add(pendingEdit);
            }

            // Process updates
            foreach (var feature in edits.Updates)
            {
                var pendingEdit = new PendingEditEntity
                {
                    ServiceId = serviceId,
                    LayerId = layerId,
                    OperationType = "Update",
                    OriginalObjectId = feature.Id,
                    FeatureData = SerializeFeature(feature),
                    CreatedAt = DateTime.UtcNow,
                    IsSynced = false
                };

                _dbContext.PendingEdits.Add(pendingEdit);
                updateEdits.Add(pendingEdit);
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
                deleteEdits.Add(pendingEdit);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var addResults = addEdits.Select(CreatePendingEditOperationResult).ToImmutableArray();
            var updateResults = updateEdits.Select(CreatePendingEditOperationResult).ToImmutableArray();
            var deleteResults = deleteEdits.Select(CreatePendingEditOperationResult).ToImmutableArray();

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
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var pendingEditIdList = objectIds.Distinct().ToList();
            _logger.LogDebug("Marking {EditCount} edits as synced", pendingEditIdList.Count);

            var pendingEdits = await _dbContext.PendingEdits
                .Where(pe => pendingEditIdList.Contains(pe.Id))
                .ToListAsync(cancellationToken);

            foreach (var edit in pendingEdits)
            {
                edit.IsSynced = true;
                edit.SyncedAt = DateTime.UtcNow;
                edit.LastSyncError = null;
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, cancellationToken);
        var token = combinedCts.Token;

        var pendingEdits = await _dbContext.PendingEdits
            .Where(pe => !pe.IsSynced)
            .OrderBy(pe => pe.CreatedAt)
            .ThenBy(pe => pe.Id)
            .ToListAsync(token);

        if (pendingEdits.Count == 0)
        {
            return new SyncResult();
        }

        var result = new SyncResult();
        var processedOperations = 0;
        context.ProgressReporter?.Report(SyncProgress.Step(
            "Sync",
            0,
            pendingEdits.Count,
            $"Syncing {pendingEdits.Count} pending edits..."));

        foreach (var group in pendingEdits.GroupBy(pe => new { pe.ServiceId, pe.LayerId }))
        {
            token.ThrowIfCancellationRequested();

            var groupedEdits = group.ToList();
            var featureEdits = BuildFeatureEdits(groupedEdits);

            try
            {
                var networkResult = await _networkClient.ApplyEditsAsync(
                    group.Key.ServiceId,
                    group.Key.LayerId,
                    featureEdits,
                    context,
                    token);

                ApplySyncOutcome(groupedEdits, networkResult, result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                foreach (var pendingEdit in groupedEdits)
                {
                    pendingEdit.SyncAttempts++;
                    pendingEdit.LastSyncError = ex.Message;
                    result.FailedOperations++;
                    result.Errors.Add(new SyncError
                    {
                        ObjectId = pendingEdit.OriginalObjectId ?? pendingEdit.Id,
                        OperationType = pendingEdit.OperationType,
                        Message = ex.Message,
                        Exception = ex
                    });
                }
            }

            processedOperations += groupedEdits.Count;
            context.ProgressReporter?.Report(SyncProgress.Step(
                "Sync",
                processedOperations,
                pendingEdits.Count,
                $"Synced {processedOperations} of {pendingEdits.Count} pending edits"));
        }

        await _dbContext.SaveChangesAsync(token);

        if (result.FailedOperations == 0)
        {
            context.ProgressReporter?.Report(SyncProgress.Completed(
                $"Synced {result.SyncedOperations} pending edits"));
        }
        else
        {
            context.ProgressReporter?.Report(SyncProgress.Failed(
                new InvalidOperationException("One or more pending edits failed to sync."),
                $"Synced {result.SyncedOperations} edits, {result.FailedOperations} failed"));
        }

        return result;
    }

    /// <summary>
    /// Downloads an area for offline use.
    /// </summary>
    public async Task<DownloadResult> DownloadAreaAsync(
        string serviceId,
        int layerId,
        CoreEnvelope boundingBox,
        MobileContext context,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, cancellationToken);
        var token = combinedCts.Token;
        var startedAt = DateTime.UtcNow;
        var featuresDownloaded = 0;
        long dataSizeBytes = 0;

        var spatialFilter = CreateBoundingBoxFilter(boundingBox);
        var query = new FeatureQuery
        {
            SpatialFilter = spatialFilter,
            ResultRecordCount = _options.MobilePageSize
        };

        try
        {
            await foreach (var page in _networkClient.QueryFeaturesStreamAsync(
                serviceId,
                layerId,
                query,
                context,
                token))
            {
                if (page.Features.Any())
                {
                    await CacheFeaturesAsync(serviceId, layerId, page.Features, token);
                    featuresDownloaded += page.Features.Length;
                    dataSizeBytes += EstimateFeatureSize(page.Features);
                }
            }

            return new DownloadResult
            {
                FeaturesDownloaded = featuresDownloaded,
                DataSizeBytes = dataSizeBytes,
                Duration = DateTime.UtcNow - startedAt,
                IsSuccess = true
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to download offline area for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);
            return new DownloadResult
            {
                FeaturesDownloaded = featuresDownloaded,
                DataSizeBytes = dataSizeBytes,
                Duration = DateTime.UtcNow - startedAt,
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Checks if cached data exists for the specified service and layer.
    /// </summary>
    public async Task<bool> HasCachedDataAsync(
        string serviceId,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

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
        ObjectDisposedException.ThrowIf(_disposed, this);

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
        ObjectDisposedException.ThrowIf(_disposed, this);

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
        return JsonSerializer.Serialize(new
        {
            feature.Id,
            feature.Attributes,
            GeometryWkt = feature.Geometry is { Length: > 0 }
                ? GeometryConverter.FromWkb(feature.Geometry).AsText()
                : null
        }, SerializerOptions);
    }

    private static DomainFeature DeserializeFeature(string featureData)
    {
        var storedFeature = JsonSerializer.Deserialize<StoredFeature>(featureData, SerializerOptions)
            ?? throw new InvalidOperationException("Pending edit payload is invalid.");

        var attributes = storedFeature.Attributes is { Count: > 0 }
            ? storedFeature.Attributes.ToImmutableDictionary(
                kvp => kvp.Key,
                kvp => ConvertJsonValue(kvp.Value))
            : ImmutableDictionary<string, object?>.Empty;

        Geometry? geometry = null;
        if (!string.IsNullOrWhiteSpace(storedFeature.GeometryWkt))
        {
            var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
            var wktReader = new WKTReader(geometryFactory);
            geometry = wktReader.Read(storedFeature.GeometryWkt);
        }

        return new DomainFeature
        {
            Id = storedFeature.Id,
            Attributes = attributes,
            Geometry = geometry is null ? null : GeometryConverter.ToWkb(geometry)
        };
    }

    private static object? ConvertJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var intValue) => intValue,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertJsonValue).ToArray(),
            JsonValueKind.Object => value.EnumerateObject()
                .ToDictionary(property => property.Name, property => ConvertJsonValue(property.Value)),
            _ => value.ToString()
        };
    }

    private static OperationResult CreatePendingEditOperationResult(PendingEditEntity pendingEdit)
        => new()
        {
            ObjectId = pendingEdit.Id,
            Success = true
        };

    private static FeatureEdits BuildFeatureEdits(IReadOnlyCollection<PendingEditEntity> pendingEdits)
    {
        return new FeatureEdits
        {
            Adds = pendingEdits
                .Where(pe => string.Equals(pe.OperationType, "Add", StringComparison.OrdinalIgnoreCase))
                .Select(pe => DeserializeFeature(pe.FeatureData ?? throw new InvalidOperationException(
                    $"Pending add {pe.Id} is missing feature data.")))
                .ToImmutableArray(),
            Updates = pendingEdits
                .Where(pe => string.Equals(pe.OperationType, "Update", StringComparison.OrdinalIgnoreCase))
                .Select(pe => DeserializeFeature(pe.FeatureData ?? throw new InvalidOperationException(
                    $"Pending update {pe.Id} is missing feature data.")))
                .ToImmutableArray(),
            Deletes = pendingEdits
                .Where(pe => string.Equals(pe.OperationType, "Delete", StringComparison.OrdinalIgnoreCase))
                .Select(pe => pe.OriginalObjectId ?? throw new InvalidOperationException(
                    $"Pending delete {pe.Id} is missing the original object ID."))
                .ToImmutableArray()
        };
    }

    private void ApplySyncOutcome(
        IReadOnlyList<PendingEditEntity> pendingEdits,
        EditResult networkResult,
        SyncResult syncResult)
    {
        if (networkResult.Error != null)
        {
            foreach (var pendingEdit in pendingEdits)
            {
                pendingEdit.SyncAttempts++;
                pendingEdit.LastSyncError = networkResult.Error.Message;
                syncResult.FailedOperations++;
                syncResult.Errors.Add(new SyncError
                {
                    ObjectId = pendingEdit.OriginalObjectId ?? pendingEdit.Id,
                    OperationType = pendingEdit.OperationType,
                    Message = networkResult.Error.Message
                });
            }

            return;
        }

        ApplyOperationResults(
            pendingEdits.Where(pe => string.Equals(pe.OperationType, "Add", StringComparison.OrdinalIgnoreCase)).ToList(),
            networkResult.AddResults,
            syncResult);
        ApplyOperationResults(
            pendingEdits.Where(pe => string.Equals(pe.OperationType, "Update", StringComparison.OrdinalIgnoreCase)).ToList(),
            networkResult.UpdateResults,
            syncResult);
        ApplyOperationResults(
            pendingEdits.Where(pe => string.Equals(pe.OperationType, "Delete", StringComparison.OrdinalIgnoreCase)).ToList(),
            networkResult.DeleteResults,
            syncResult);
    }

    private static void ApplyOperationResults(
        IReadOnlyList<PendingEditEntity> pendingEdits,
        ImmutableArray<OperationResult> operationResults,
        SyncResult syncResult)
    {
        for (var index = 0; index < pendingEdits.Count; index++)
        {
            var pendingEdit = pendingEdits[index];
            var operationResult = index < operationResults.Length
                ? operationResults[index]
                : new OperationResult
                {
                    Success = false,
                    Error = new EditError
                    {
                        Code = -1,
                        Message = "Sync result was missing an operation response."
                    }
                };

            if (operationResult.Success)
            {
                pendingEdit.IsSynced = true;
                pendingEdit.SyncedAt = DateTime.UtcNow;
                pendingEdit.LastSyncError = null;
                syncResult.SyncedOperations++;
            }
            else
            {
                pendingEdit.SyncAttempts++;
                pendingEdit.LastSyncError = operationResult.Error?.Message ?? "Unknown sync failure";
                syncResult.FailedOperations++;
                syncResult.Errors.Add(new SyncError
                {
                    ObjectId = pendingEdit.OriginalObjectId ?? pendingEdit.Id,
                    OperationType = pendingEdit.OperationType,
                    Message = pendingEdit.LastSyncError
                });
            }
        }
    }

    private static SpatialFilter CreateBoundingBoxFilter(CoreEnvelope boundingBox)
    {
        var srid = boundingBox.SpatialReference?.LatestWKID
            ?? boundingBox.SpatialReference?.WKID
            ?? 4326;

        var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: srid);
        var polygon = geometryFactory.CreatePolygon(
        [
            new Coordinate(boundingBox.XMin, boundingBox.YMin),
            new Coordinate(boundingBox.XMax, boundingBox.YMin),
            new Coordinate(boundingBox.XMax, boundingBox.YMax),
            new Coordinate(boundingBox.XMin, boundingBox.YMax),
            new Coordinate(boundingBox.XMin, boundingBox.YMin)
        ]);

        return SpatialFilter.Create(
            GeometryConverter.ToWkb(polygon),
            SpatialRelationship.Intersects,
            srid);
    }

    private static long EstimateFeatureSize(ImmutableArray<DomainFeature> features)
        => features.Sum(feature =>
            (feature.Geometry?.LongLength ?? 0L) +
            JsonSerializer.Serialize(feature.Attributes, SerializerOptions).Length);

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

internal static class FeatureQueryOfflineSupport
{
    public static bool TryGetUnsupportedReason(FeatureQuery featureQuery, out string? reason)
    {
        if (!string.IsNullOrWhiteSpace(featureQuery.Where) && !IsTrivialWhere(featureQuery.Where))
        {
            reason = "WHERE clauses other than '1=1' are not supported by offline storage.";
            return true;
        }

        if (featureQuery.SqlFilter is not null)
        {
            reason = "Parameterized SQL filters are not supported by offline storage.";
            return true;
        }

        if (featureQuery.SpatialFilter is not null)
        {
            reason = "Spatial filters are not supported by offline storage.";
            return true;
        }

        if (featureQuery.TemporalFilter is not null)
        {
            reason = "Temporal filters are not supported by offline storage.";
            return true;
        }

        if (featureQuery.Distinct)
        {
            reason = "Distinct queries are not supported by offline storage.";
            return true;
        }

        if (HasValues(featureQuery.OutStatistics))
        {
            reason = "Statistical queries are not supported by offline storage.";
            return true;
        }

        if (HasValues(featureQuery.GroupByFields))
        {
            reason = "Grouped statistics are not supported by offline storage.";
            return true;
        }

        if (featureQuery.TopFilter is not null)
        {
            reason = "Top feature queries are not supported by offline storage.";
            return true;
        }

        if (HasValues(featureQuery.OrderBy) && !IsObjectIdOrdering(featureQuery.OrderBy!.Value))
        {
            reason = "Only ObjectId ordering is supported by offline storage.";
            return true;
        }

        reason = null;
        return false;
    }

    public static void EnsureSupported(FeatureQuery featureQuery)
    {
        if (TryGetUnsupportedReason(featureQuery, out var reason))
        {
            throw new NotSupportedException(reason);
        }
    }

    public static DomainFeature ProjectFeature(DomainFeature feature, FeatureQuery query)
    {
        if (query.OutFields is not { } outFields || outFields.IsDefaultOrEmpty)
        {
            return feature;
        }

        if (outFields.Any(field => string.Equals(field, "*", StringComparison.Ordinal)))
        {
            return feature;
        }

        var projectedAttributes = feature.Attributes
            .Where(kvp => outFields.Contains(kvp.Key))
            .ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return feature with { Attributes = projectedAttributes };
    }

    private static bool IsTrivialWhere(string where)
    {
        var normalized = where.Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 0 ||
               string.Equals(normalized, "1=1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsObjectIdOrdering(ImmutableArray<OrderByClause> orderBy)
    {
        if (orderBy.IsDefaultOrEmpty)
        {
            return true;
        }

        return orderBy.All(clause =>
            string.Equals(clause.Field, "objectid", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clause.Field, "id", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasValues<T>(ImmutableArray<T>? values)
        => values is { } array && !array.IsDefaultOrEmpty;
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
        if (featureQuery.ObjectIds is { } objectIds && !objectIds.IsDefaultOrEmpty)
        {
            query = query.Where(cachedFeature => objectIds.Contains(cachedFeature.ObjectId));
        }

        if (featureQuery.OrderBy is { } orderBy && !orderBy.IsDefaultOrEmpty)
        {
            var primaryOrder = orderBy[0];
            query = primaryOrder.Ascending
                ? query.OrderBy(cf => cf.ObjectId)
                : query.OrderByDescending(cf => cf.ObjectId);
        }
        else
        {
            query = query.OrderBy(cf => cf.ObjectId);
        }

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

internal sealed class StoredFeature
{
    public long Id { get; set; }

    public Dictionary<string, JsonElement>? Attributes { get; set; }

    public string? GeometryWkt { get; set; }
}
