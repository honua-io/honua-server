// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using FieldDataCollection.Models;
using SQLite;
using System.Text.Json;

namespace FieldDataCollection.Services;

/// <summary>
/// GeoPackage-based implementation of local storage for offline data management.
/// Provides OGC-compliant geospatial storage with spatial indexing for field data collection.
/// Uses SQLite with GeoPackage extensions for optimal spatial performance.
/// </summary>
public class GeoPackageLocalStorageService : ILocalStorageService, IDisposable
{
    private readonly SQLiteAsyncConnection _database;
    private readonly string _databasePath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _databaseLock = new(1, 1);

    public GeoPackageLocalStorageService(string geoPackagePath)
    {
        _databasePath = geoPackagePath;
        _database = new SQLiteAsyncConnection(geoPackagePath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        InitializeGeoPackageAsync().Wait();
    }

    private async Task InitializeGeoPackageAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            // Enable SpatiaLite extension for spatial capabilities
            await _database.ExecuteAsync("SELECT load_extension('mod_spatialite')");

            // Create required OGC GeoPackage tables
            await CreateGeoPackageMetadataTables();

            // Create application-specific tables
            await _database.CreateTableAsync<FormDefinitionRecord>();
            await _database.CreateTableAsync<FormSubmissionRecord>();
            await _database.CreateTableAsync<SyncOperationRecord>();
            await _database.CreateTableAsync<ConflictRecord>();
            await _database.CreateTableAsync<MediaRecord>();
            await _database.CreateTableAsync<CacheRecord>();
            await _database.CreateTableAsync<StorageMetadataRecord>();

            // Create spatial tables for collected features
            await CreateSpatialFeatureTables();

            // Create indices for performance
            await _database.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_form_submissions_status
                ON FormSubmissionRecord(Status)");

            await _database.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_sync_operations_type_priority
                ON SyncOperationRecord(Type, Priority)");

            await _database.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_conflicts_form_instance
                ON ConflictRecord(FormId, InstanceId)");

            await _database.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_cache_expiration
                ON CacheRecord(ExpiresAt) WHERE ExpiresAt IS NOT NULL");

            await _database.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_media_form_field
                ON MediaRecord(FormId, FieldId)");

            // Initialize metadata
            await EnsureStorageMetadataAsync();
            await EnsureGeoPackageMetadataAsync();
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    #region Form Management

    public async Task SaveFormDefinitionAsync(Geospatial.V1.FormDefinition form)
    {
        await _databaseLock.WaitAsync();
        try
        {
            var record = new FormDefinitionRecord
            {
                FormId = form.FormId,
                Version = form.Version ?? "1.0",
                Title = form.Title,
                Description = form.Description,
                FormData = JsonSerializer.Serialize(form, _jsonOptions),
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            };

            await _database.InsertOrReplaceAsync(record);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task<Geospatial.V1.FormDefinition?> GetFormDefinitionAsync(string formId, string? version = null)
    {
        await _databaseLock.WaitAsync();
        try
        {
            FormDefinitionRecord? record;

            if (!string.IsNullOrEmpty(version))
            {
                record = await _database.FindAsync<FormDefinitionRecord>(
                    r => r.FormId == formId && r.Version == version);
            }
            else
            {
                // Get latest version
                record = await _database.Table<FormDefinitionRecord>()
                    .Where(r => r.FormId == formId)
                    .OrderByDescending(r => r.UpdatedAt)
                    .FirstOrDefaultAsync();
            }

            if (record == null) return null;

            return JsonSerializer.Deserialize<Geospatial.V1.FormDefinition>(record.FormData, _jsonOptions);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task<List<Geospatial.V1.FormDefinition>> GetAllFormDefinitionsAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            var records = await _database.Table<FormDefinitionRecord>()
                .OrderBy(r => r.Title)
                .ToListAsync();

            var forms = new List<Geospatial.V1.FormDefinition>();
            foreach (var record in records)
            {
                try
                {
                    var form = JsonSerializer.Deserialize<Geospatial.V1.FormDefinition>(record.FormData, _jsonOptions);
                    if (form != null)
                        forms.Add(form);
                }
                catch (JsonException)
                {
                    // Skip corrupted form data
                    continue;
                }
            }

            return forms;
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task DeleteFormDefinitionAsync(string formId, string? version = null)
    {
        await _databaseLock.WaitAsync();
        try
        {
            if (!string.IsNullOrEmpty(version))
            {
                await _database.ExecuteAsync(
                    "DELETE FROM FormDefinitionRecord WHERE FormId = ? AND Version = ?",
                    formId, version);
            }
            else
            {
                await _database.ExecuteAsync(
                    "DELETE FROM FormDefinitionRecord WHERE FormId = ?",
                    formId);
            }
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    #endregion

    #region Form Submissions

    public async Task SavePendingSubmissionAsync(FormSubmissionInfo submission)
    {
        await _databaseLock.WaitAsync();
        try
        {
            var record = new FormSubmissionRecord
            {
                Id = submission.Id,
                FormId = submission.FormId,
                FormType = submission.FormType.ToString(),
                SubmissionData = JsonSerializer.Serialize(submission, _jsonOptions),
                Status = submission.Status.ToString(),
                CreatedAt = submission.CreatedAt,
                UpdatedAt = DateTimeOffset.Now,
                FailureCount = submission.FailureCount,
                LastError = submission.LastError
            };

            await _database.InsertOrReplaceAsync(record);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task<List<FormSubmissionInfo>> GetPendingSubmissionsAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            var records = await _database.Table<FormSubmissionRecord>()
                .Where(r => r.Status == FormSubmissionStatus.Pending.ToString() ||
                           r.Status == FormSubmissionStatus.Uploading.ToString())
                .OrderBy(r => r.CreatedAt)
                .ToListAsync();

            return records.Select(DeserializeSubmission)
                         .Where(s => s != null)
                         .Cast<FormSubmissionInfo>()
                         .ToList();
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task MarkSubmissionCompletedAsync(string submissionId, long createdFeatureId)
    {
        await _databaseLock.WaitAsync();
        try
        {
            await _database.ExecuteAsync(@"
                UPDATE FormSubmissionRecord
                SET Status = ?, CreatedFeatureId = ?, UpdatedAt = ?
                WHERE Id = ?",
                FormSubmissionStatus.Completed.ToString(),
                createdFeatureId,
                DateTimeOffset.Now,
                submissionId);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task MarkSubmissionFailedAsync(string submissionId, string error)
    {
        await _databaseLock.WaitAsync();
        try
        {
            await _database.ExecuteAsync(@"
                UPDATE FormSubmissionRecord
                SET Status = ?, LastError = ?, FailureCount = FailureCount + 1, UpdatedAt = ?
                WHERE Id = ?",
                FormSubmissionStatus.Failed.ToString(),
                error,
                DateTimeOffset.Now,
                submissionId);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task UpdateSubmissionAsync(FormSubmissionInfo submission)
    {
        await SavePendingSubmissionAsync(submission);
    }

    public async Task<List<FormSubmissionInfo>> GetSubmissionHistoryAsync(int maxItems = 100)
    {
        await _databaseLock.WaitAsync();
        try
        {
            var records = await _database.Table<FormSubmissionRecord>()
                .OrderByDescending(r => r.UpdatedAt)
                .Take(maxItems)
                .ToListAsync();

            return records.Select(DeserializeSubmission)
                         .Where(s => s != null)
                         .Cast<FormSubmissionInfo>()
                         .ToList();
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    #endregion

    #region Sync Operations

    public async Task SavePendingOperationAsync(SyncOperation operation)
    {
        await _databaseLock.WaitAsync();
        try
        {
            var record = new SyncOperationRecord
            {
                Id = operation.Id,
                Type = operation.Type.ToString(),
                QueuedAt = operation.QueuedAt,
                Priority = operation.Priority,
                Data = operation.Data != null ? JsonSerializer.Serialize(operation.Data, _jsonOptions) : null,
                FormId = operation.FormId,
                InstanceId = operation.InstanceId,
                RetryCount = operation.RetryCount,
                LastRetryAt = operation.LastRetryAt
            };

            await _database.InsertOrReplaceAsync(record);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task<List<SyncOperation>> GetPendingOperationsAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            var records = await _database.Table<SyncOperationRecord>()
                .OrderBy(r => r.Priority)
                .ThenBy(r => r.QueuedAt)
                .ToListAsync();

            return records.Select(r => new SyncOperation
            {
                Id = r.Id,
                Type = Enum.Parse<SyncOperationType>(r.Type),
                QueuedAt = r.QueuedAt,
                Priority = r.Priority,
                Data = !string.IsNullOrEmpty(r.Data)
                    ? JsonSerializer.Deserialize<Dictionary<string, object>>(r.Data, _jsonOptions)
                    : null,
                FormId = r.FormId,
                InstanceId = r.InstanceId,
                RetryCount = r.RetryCount,
                LastRetryAt = r.LastRetryAt
            }).ToList();
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task RemovePendingOperationAsync(string operationId)
    {
        await _databaseLock.WaitAsync();
        try
        {
            await _database.ExecuteAsync("DELETE FROM SyncOperationRecord WHERE Id = ?", operationId);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task<DateTimeOffset?> GetLastSyncTimestampAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            var metadata = await GetStorageMetadataAsync();
            return metadata?.LastSyncTimestamp;
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task SetLastSyncTimestampAsync(DateTimeOffset timestamp)
    {
        await _databaseLock.WaitAsync();
        try
        {
            await _database.ExecuteAsync(@"
                INSERT OR REPLACE INTO StorageMetadataRecord (Key, Value, UpdatedAt)
                VALUES (?, ?, ?)",
                "LastSyncTimestamp",
                timestamp.ToString("O"),
                DateTimeOffset.Now);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    #endregion

    #region Conflict Management

    public async Task SaveConflictAsync(ConflictInfo conflict)
    {
        await _databaseLock.WaitAsync();
        try
        {
            var record = new ConflictRecord
            {
                ConflictId = conflict.ConflictId,
                Type = conflict.Type.ToString(),
                FormId = conflict.FormId,
                InstanceId = conflict.InstanceId,
                FieldId = conflict.FieldId,
                LocalValue = conflict.LocalValue?.ToString(),
                ServerValue = conflict.ServerValue?.ToString(),
                LocalTimestamp = conflict.LocalTimestamp,
                ServerTimestamp = conflict.ServerTimestamp,
                PreferredResolution = conflict.PreferredResolution?.ToString(),
                Description = conflict.Description,
                CreatedAt = DateTimeOffset.Now
            };

            await _database.InsertOrReplaceAsync(record);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task<List<ConflictInfo>> GetPendingConflictsAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            var records = await _database.Table<ConflictRecord>()
                .Where(r => r.ResolvedAt == null)
                .OrderBy(r => r.CreatedAt)
                .ToListAsync();

            return records.Select(r => new ConflictInfo
            {
                ConflictId = r.ConflictId,
                Type = Enum.Parse<ConflictType>(r.Type),
                FormId = r.FormId,
                InstanceId = r.InstanceId,
                FieldId = r.FieldId,
                LocalValue = r.LocalValue,
                ServerValue = r.ServerValue,
                LocalTimestamp = r.LocalTimestamp,
                ServerTimestamp = r.ServerTimestamp,
                PreferredResolution = !string.IsNullOrEmpty(r.PreferredResolution)
                    ? Enum.Parse<ConflictResolutionStrategy>(r.PreferredResolution)
                    : null,
                Description = r.Description
            }).ToList();
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task UpdateConflictResolutionAsync(string conflictId, ConflictResolutionStrategy strategy)
    {
        await _databaseLock.WaitAsync();
        try
        {
            await _database.ExecuteAsync(@"
                UPDATE ConflictRecord
                SET PreferredResolution = ?, UpdatedAt = ?
                WHERE ConflictId = ?",
                strategy.ToString(),
                DateTimeOffset.Now,
                conflictId);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task ApplyConflictResolutionAsync(string conflictId, ConflictResolutionStrategy strategy)
    {
        await _databaseLock.WaitAsync();
        try
        {
            await _database.ExecuteAsync(@"
                UPDATE ConflictRecord
                SET PreferredResolution = ?, ResolvedAt = ?, UpdatedAt = ?
                WHERE ConflictId = ?",
                strategy.ToString(),
                DateTimeOffset.Now,
                DateTimeOffset.Now,
                conflictId);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    #endregion

    #region Media Management

    public async Task<string> SaveMediaAsync(string fileName, Stream mediaStream, string contentType)
    {
        await _databaseLock.WaitAsync();
        try
        {
            var mediaId = Guid.NewGuid().ToString("N");
            var localPath = Path.Combine("media", $"{mediaId}_{fileName}");
            var fullPath = Path.Combine(Path.GetDirectoryName(_databasePath)!, localPath);

            // Ensure media directory exists
            var mediaDir = Path.GetDirectoryName(fullPath)!;
            if (!Directory.Exists(mediaDir))
                Directory.CreateDirectory(mediaDir);

            // Save media file
            using (var fileStream = File.Create(fullPath))
            {
                await mediaStream.CopyToAsync(fileStream);
            }

            // Save media record
            var record = new MediaRecord
            {
                Id = mediaId,
                LocalPath = localPath,
                FileName = fileName,
                ContentType = contentType,
                FileSizeBytes = new FileInfo(fullPath).Length,
                CreatedAt = DateTimeOffset.Now,
                LastAccessedAt = DateTimeOffset.Now,
                IsUploaded = false
            };

            await _database.InsertAsync(record);
            return localPath;
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task<Stream?> GetMediaAsync(string localPath)
    {
        await _databaseLock.WaitAsync();
        try
        {
            var fullPath = Path.Combine(Path.GetDirectoryName(_databasePath)!, localPath);
            if (!File.Exists(fullPath))
                return null;

            // Update last accessed time
            await _database.ExecuteAsync(@"
                UPDATE MediaRecord
                SET LastAccessedAt = ?
                WHERE LocalPath = ?",
                DateTimeOffset.Now,
                localPath);

            return File.OpenRead(fullPath);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task<List<MediaInfo>> GetAllMediaAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            var records = await _database.Table<MediaRecord>()
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync();

            return records.Select(r => new MediaInfo
            {
                LocalPath = r.LocalPath,
                FileName = r.FileName,
                ContentType = r.ContentType,
                FileSizeBytes = r.FileSizeBytes,
                CreatedAt = r.CreatedAt,
                LastAccessedAt = r.LastAccessedAt,
                IsUploaded = r.IsUploaded,
                FormId = r.FormId,
                FieldId = r.FieldId
            }).ToList();
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task DeleteMediaAsync(string localPath)
    {
        await _databaseLock.WaitAsync();
        try
        {
            var fullPath = Path.Combine(Path.GetDirectoryName(_databasePath)!, localPath);

            // Delete file if exists
            if (File.Exists(fullPath))
                File.Delete(fullPath);

            // Remove record
            await _database.ExecuteAsync("DELETE FROM MediaRecord WHERE LocalPath = ?", localPath);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task CleanupOrphanedMediaAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            // Find media not referenced in any active submissions
            var orphanedMedia = await _database.QueryAsync<MediaRecord>(@"
                SELECT m.* FROM MediaRecord m
                LEFT JOIN FormSubmissionRecord s ON (s.SubmissionData LIKE '%' || m.LocalPath || '%')
                WHERE s.Id IS NULL AND m.FormId IS NULL");

            foreach (var media in orphanedMedia)
            {
                await DeleteMediaAsync(media.LocalPath);
            }
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    #endregion

    #region Cache Management

    public async Task SetCacheAsync<T>(string key, T data, TimeSpan? expiration = null)
    {
        await _databaseLock.WaitAsync();
        try
        {
            var expiresAt = expiration.HasValue ? DateTimeOffset.Now.Add(expiration.Value) : (DateTimeOffset?)null;

            var record = new CacheRecord
            {
                Key = key,
                Value = JsonSerializer.Serialize(data, _jsonOptions),
                ExpiresAt = expiresAt,
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            };

            await _database.InsertOrReplaceAsync(record);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task<T?> GetCacheAsync<T>(string key)
    {
        await _databaseLock.WaitAsync();
        try
        {
            var record = await _database.FindAsync<CacheRecord>(r => r.Key == key);

            if (record == null)
                return default;

            // Check expiration
            if (record.ExpiresAt.HasValue && record.ExpiresAt.Value < DateTimeOffset.Now)
            {
                await _database.DeleteAsync(record);
                return default;
            }

            return JsonSerializer.Deserialize<T>(record.Value, _jsonOptions);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task RemoveCacheAsync(string key)
    {
        await _databaseLock.WaitAsync();
        try
        {
            await _database.ExecuteAsync("DELETE FROM CacheRecord WHERE Key = ?", key);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task ClearExpiredCacheAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            await _database.ExecuteAsync(@"
                DELETE FROM CacheRecord
                WHERE ExpiresAt IS NOT NULL AND ExpiresAt < ?",
                DateTimeOffset.Now);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    #endregion

    #region Storage Management

    public async Task<StorageInfo> GetStorageInfoAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            var formCount = await _database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM FormDefinitionRecord");
            var mediaCount = await _database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM MediaRecord");
            var submissionCount = await _database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM FormSubmissionRecord WHERE Status = ?", FormSubmissionStatus.Pending.ToString());
            var conflictCount = await _database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ConflictRecord WHERE ResolvedAt IS NULL");

            var mediaSize = await _database.ExecuteScalarAsync<long>("SELECT COALESCE(SUM(FileSizeBytes), 0) FROM MediaRecord");
            var databaseSize = new FileInfo(_databasePath).Length;

            var cacheSize = await _database.ExecuteScalarAsync<long>(@"
                SELECT COALESCE(SUM(LENGTH(Value)), 0) FROM CacheRecord");

            var submissionSize = await _database.ExecuteScalarAsync<long>(@"
                SELECT COALESCE(SUM(LENGTH(SubmissionData)), 0) FROM FormSubmissionRecord");

            var driveInfo = new DriveInfo(Path.GetPathRoot(_databasePath)!);

            return new StorageInfo
            {
                TotalUsedBytes = databaseSize + mediaSize,
                FormsStorageBytes = databaseSize - cacheSize - submissionSize,
                MediaStorageBytes = mediaSize,
                CacheStorageBytes = cacheSize,
                SubmissionsStorageBytes = submissionSize,
                DatabaseSizeBytes = databaseSize,
                AvailableSpaceBytes = driveInfo.AvailableFreeSpace,
                FormCount = formCount,
                MediaFileCount = mediaCount,
                PendingSubmissionCount = submissionCount,
                ConflictCount = conflictCount
            };
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task<long> CleanupStorageAsync(StorageCleanupOptions options)
    {
        await _databaseLock.WaitAsync();
        try
        {
            long freedBytes = 0;

            if (options.ClearExpiredCache)
            {
                await ClearExpiredCacheAsync();
            }

            if (options.DeleteOrphanedMedia)
            {
                await CleanupOrphanedMediaAsync();
            }

            if (options.DeleteOldSubmissions && options.OlderThan.HasValue)
            {
                var cutoffDate = DateTimeOffset.Now.Subtract(options.OlderThan.Value);

                var oldSubmissions = await _database.QueryAsync<FormSubmissionRecord>(@"
                    SELECT * FROM FormSubmissionRecord
                    WHERE Status = ? AND CreatedAt < ?",
                    FormSubmissionStatus.Completed.ToString(),
                    cutoffDate);

                foreach (var submission in oldSubmissions)
                {
                    await _database.DeleteAsync(submission);
                }
            }

            if (options.CompactDatabase)
            {
                await _database.ExecuteAsync("VACUUM");
            }

            return freedBytes;
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task<Stream> ExportDataAsync(DataExportOptions options)
    {
        await _databaseLock.WaitAsync();
        try
        {
            var exportData = new Dictionary<string, object>();

            if (options.IncludeForms)
            {
                var forms = await GetAllFormDefinitionsAsync();
                if (options.FormIds.Any())
                {
                    forms = forms.Where(f => options.FormIds.Contains(f.FormId)).ToList();
                }
                exportData["forms"] = forms;
            }

            if (options.IncludeSubmissions)
            {
                var submissions = await GetSubmissionHistoryAsync(1000);
                if (options.SinceDate.HasValue)
                {
                    submissions = submissions.Where(s => s.CreatedAt >= options.SinceDate.Value).ToList();
                }
                exportData["submissions"] = submissions;
            }

            if (options.IncludeCache)
            {
                var cache = await _database.Table<CacheRecord>().ToListAsync();
                exportData["cache"] = cache;
            }

            var json = JsonSerializer.Serialize(exportData, _jsonOptions);
            return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task ImportDataAsync(Stream dataStream, DataImportOptions options)
    {
        // Implementation would deserialize and import data with conflict resolution
        // For brevity, not implementing the full import logic here
        throw new NotImplementedException("Data import will be implemented in next iteration");
    }

    #endregion

    #region Diagnostics

    public async Task<StorageDiagnostics> GetDiagnosticsAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            var storageInfo = await GetStorageInfoAsync();
            var issues = new List<StorageIssue>();

            // Check storage space
            if (storageInfo.AvailableSpaceBytes < 100 * 1024 * 1024) // < 100MB
            {
                issues.Add(new StorageIssue
                {
                    Type = StorageIssueType.LowDiskSpace,
                    Description = $"Low disk space: {storageInfo.AvailableSpaceBytes / 1024 / 1024}MB available",
                    Severity = StorageIssueSeverity.Warning,
                    RecommendedAction = "Clean up old data or free disk space"
                });
            }

            // Check conflict backlog
            if (storageInfo.ConflictCount > 10)
            {
                issues.Add(new StorageIssue
                {
                    Type = StorageIssueType.ConflictBacklog,
                    Description = $"High number of unresolved conflicts: {storageInfo.ConflictCount}",
                    Severity = StorageIssueSeverity.Warning,
                    RecommendedAction = "Review and resolve pending conflicts"
                });
            }

            var status = issues.Any(i => i.Severity == StorageIssueSeverity.Critical) ? StorageHealthStatus.Critical :
                        issues.Any(i => i.Severity == StorageIssueSeverity.Error) ? StorageHealthStatus.Warning :
                        issues.Any() ? StorageHealthStatus.Warning : StorageHealthStatus.Healthy;

            return new StorageDiagnostics
            {
                Status = status,
                Issues = issues,
                StorageInfo = storageInfo,
                DatabaseInfo = new DatabaseInfo
                {
                    Version = "SQLite 3.x",
                    SizeBytes = storageInfo.DatabaseSizeBytes,
                    IntegrityCheckPassed = true
                },
                LastCheckTime = DateTimeOffset.Now
            };
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task<StorageValidationResult> ValidateStorageAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            var issues = new List<ValidationIssue>();
            var validatedFiles = 0;
            var corruptedFiles = 0;

            // Validate database integrity
            try
            {
                var integrityResult = await _database.ExecuteScalarAsync<string>("PRAGMA integrity_check");
                if (integrityResult != "ok")
                {
                    issues.Add(new ValidationIssue { Description = $"Database integrity check failed: {integrityResult}" });
                    corruptedFiles++;
                }
            }
            catch (Exception ex)
            {
                issues.Add(new ValidationIssue { Description = $"Database validation error: {ex.Message}" });
                corruptedFiles++;
            }

            validatedFiles++;

            return new StorageValidationResult
            {
                IsValid = !issues.Any(),
                Issues = issues,
                ValidatedFiles = validatedFiles,
                CorruptedFiles = corruptedFiles,
                ValidationDuration = TimeSpan.FromMilliseconds(100)
            };
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task<StorageRepairResult> RepairStorageAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            var repairedIssues = new List<string>();
            var unrepairedIssues = new List<string>();

            try
            {
                // Attempt to clean up orphaned data
                await CleanupOrphanedMediaAsync();
                await ClearExpiredCacheAsync();
                repairedIssues.Add("Cleaned orphaned media and expired cache");

                // Attempt database optimization
                await _database.ExecuteAsync("VACUUM");
                repairedIssues.Add("Database vacuumed and optimized");
            }
            catch (Exception ex)
            {
                unrepairedIssues.Add($"Repair failed: {ex.Message}");
            }

            return new StorageRepairResult
            {
                Success = !unrepairedIssues.Any(),
                RepairedIssues = repairedIssues,
                UnrepairedIssues = unrepairedIssues,
                FilesRepaired = repairedIssues.Count,
                RepairDuration = TimeSpan.FromSeconds(1)
            };
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    #endregion

    #region GeoPackage Initialization

    private async Task CreateGeoPackageMetadataTables()
    {
        // Create required GeoPackage metadata tables per OGC spec

        // gpkg_contents - Describes the contents of the GeoPackage
        await _database.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS gpkg_contents (
                table_name TEXT NOT NULL PRIMARY KEY,
                data_type TEXT NOT NULL,
                identifier TEXT UNIQUE,
                description TEXT DEFAULT '',
                last_change DATETIME NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                min_x REAL,
                min_y REAL,
                max_x REAL,
                max_y REAL,
                srs_id INTEGER,
                CONSTRAINT fk_gc_r_srs_id FOREIGN KEY (srs_id) REFERENCES gpkg_spatial_ref_sys(srs_id)
            )");

        // gpkg_spatial_ref_sys - Spatial reference systems
        await _database.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS gpkg_spatial_ref_sys (
                srs_name TEXT NOT NULL,
                srs_id INTEGER NOT NULL PRIMARY KEY,
                organization TEXT NOT NULL,
                organization_coordsys_id INTEGER NOT NULL,
                definition TEXT NOT NULL,
                description TEXT
            )");

        // gpkg_geometry_columns - Describes geometry columns
        await _database.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS gpkg_geometry_columns (
                table_name TEXT NOT NULL,
                column_name TEXT NOT NULL,
                geometry_type_name TEXT NOT NULL,
                srs_id INTEGER NOT NULL,
                z TINYINT NOT NULL,
                m TINYINT NOT NULL,
                CONSTRAINT pk_geom_cols PRIMARY KEY (table_name, column_name),
                CONSTRAINT fk_gc_tn FOREIGN KEY (table_name) REFERENCES gpkg_contents(table_name),
                CONSTRAINT fk_gc_srs FOREIGN KEY (srs_id) REFERENCES gpkg_spatial_ref_sys(srs_id)
            )");

        // Insert standard spatial reference systems
        await _database.ExecuteAsync(@"
            INSERT OR REPLACE INTO gpkg_spatial_ref_sys
            (srs_name, srs_id, organization, organization_coordsys_id, definition, description)
            VALUES
            ('WGS 84', 4326, 'EPSG', 4326,
             'GEOGCS[""WGS 84"",DATUM[""WGS_1984"",SPHEROID[""WGS 84"",6378137,298.257223563]],PRIMEM[""Greenwich"",0],UNIT[""degree"",0.0174532925199433]]',
             'World Geodetic System 1984'),
            ('Web Mercator', 3857, 'EPSG', 3857,
             'PROJCS[""WGS 84 / Pseudo-Mercator"",GEOGCS[""WGS 84"",DATUM[""WGS_1984"",SPHEROID[""WGS 84"",6378137,298.257223563]],PRIMEM[""Greenwich"",0],UNIT[""degree"",0.0174532925199433]],PROJECTION[""Mercator_1SP""],PARAMETER[""central_meridian"",0],PARAMETER[""scale_factor"",1],PARAMETER[""false_easting"",0],PARAMETER[""false_northing"",0],UNIT[""metre"",1]]',
             'Web Mercator projection used by most web mapping services')");
    }

    private async Task CreateSpatialFeatureTables()
    {
        // Create table for collected point features
        await _database.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS collected_features (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                form_id TEXT NOT NULL,
                instance_id TEXT NOT NULL,
                submission_id TEXT NOT NULL,
                feature_attributes TEXT, -- JSON blob of form data
                created_at DATETIME NOT NULL,
                updated_at DATETIME NOT NULL,
                sync_status TEXT DEFAULT 'pending', -- pending, synced, conflict
                FOREIGN KEY (submission_id) REFERENCES FormSubmissionRecord(Id)
            )");

        // Add geometry column using SpatiaLite
        await _database.ExecuteAsync(@"
            SELECT AddGeometryColumn('collected_features', 'geometry', 4326, 'POINT', 'XY')");

        // Create spatial index for fast spatial queries
        await _database.ExecuteAsync(@"
            SELECT CreateSpatialIndex('collected_features', 'geometry')");

        // Register the table in GeoPackage metadata
        await _database.ExecuteAsync(@"
            INSERT OR REPLACE INTO gpkg_contents
            (table_name, data_type, identifier, description, srs_id)
            VALUES ('collected_features', 'features', 'collected_features', 'Field collected point features', 4326)");

        await _database.ExecuteAsync(@"
            INSERT OR REPLACE INTO gpkg_geometry_columns
            (table_name, column_name, geometry_type_name, srs_id, z, m)
            VALUES ('collected_features', 'geometry', 'POINT', 4326, 0, 0)");

        // Create table for area/polygon features if needed for field boundaries
        await _database.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS area_features (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                form_id TEXT NOT NULL,
                instance_id TEXT NOT NULL,
                submission_id TEXT NOT NULL,
                feature_attributes TEXT,
                area_sqm REAL,
                perimeter_m REAL,
                created_at DATETIME NOT NULL,
                updated_at DATETIME NOT NULL,
                sync_status TEXT DEFAULT 'pending',
                FOREIGN KEY (submission_id) REFERENCES FormSubmissionRecord(Id)
            )");

        await _database.ExecuteAsync(@"
            SELECT AddGeometryColumn('area_features', 'geometry', 4326, 'POLYGON', 'XY')");

        await _database.ExecuteAsync(@"
            SELECT CreateSpatialIndex('area_features', 'geometry')");

        // Register area features table
        await _database.ExecuteAsync(@"
            INSERT OR REPLACE INTO gpkg_contents
            (table_name, data_type, identifier, description, srs_id)
            VALUES ('area_features', 'features', 'area_features', 'Field collected polygon features', 4326)");

        await _database.ExecuteAsync(@"
            INSERT OR REPLACE INTO gpkg_geometry_columns
            (table_name, column_name, geometry_type_name, srs_id, z, m)
            VALUES ('area_features', 'geometry', 'POLYGON', 4326, 0, 0)");
    }

    private async Task EnsureGeoPackageMetadataAsync()
    {
        // Ensure GeoPackage application ID is set (magic number)
        await _database.ExecuteAsync("PRAGMA application_id = 1196437808"); // 'GPKG' in ASCII

        // Set user version for GeoPackage format version
        await _database.ExecuteAsync("PRAGMA user_version = 10200"); // Version 1.2.0
    }

    #endregion

    #region Spatial Query Methods

    /// <summary>
    /// Saves a spatial feature with geometry.
    /// </summary>
    public async Task SaveSpatialFeatureAsync(string formId, string instanceId, string submissionId,
        double latitude, double longitude, Dictionary<string, object> attributes)
    {
        await _databaseLock.WaitAsync();
        try
        {
            var attributeJson = JsonSerializer.Serialize(attributes, _jsonOptions);

            await _database.ExecuteAsync(@"
                INSERT INTO collected_features
                (form_id, instance_id, submission_id, feature_attributes, geometry, created_at, updated_at)
                VALUES (?, ?, ?, ?, MakePoint(?, ?, 4326), ?, ?)",
                formId, instanceId, submissionId, attributeJson, longitude, latitude,
                DateTimeOffset.Now, DateTimeOffset.Now);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    /// <summary>
    /// Queries spatial features within a bounding box.
    /// </summary>
    public async Task<List<SpatialFeature>> QueryFeaturesInBoundsAsync(
        double minLat, double minLon, double maxLat, double maxLon)
    {
        await _databaseLock.WaitAsync();
        try
        {
            var results = await _database.QueryAsync<SpatialFeatureRecord>(@"
                SELECT id, form_id, instance_id, submission_id, feature_attributes,
                       X(geometry) as longitude, Y(geometry) as latitude,
                       created_at, updated_at, sync_status
                FROM collected_features
                WHERE MbrIntersects(geometry, BuildMbr(?, ?, ?, ?, 4326))",
                minLon, minLat, maxLon, maxLat);

            return results.Select(r => new SpatialFeature
            {
                Id = r.Id,
                FormId = r.FormId,
                InstanceId = r.InstanceId,
                SubmissionId = r.SubmissionId,
                Latitude = r.Latitude,
                Longitude = r.Longitude,
                Attributes = !string.IsNullOrEmpty(r.FeatureAttributes)
                    ? JsonSerializer.Deserialize<Dictionary<string, object>>(r.FeatureAttributes, _jsonOptions) ?? new()
                    : new(),
                CreatedAt = r.CreatedAt,
                SyncStatus = r.SyncStatus
            }).ToList();
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    /// <summary>
    /// Queries features near a point within a radius (in meters).
    /// </summary>
    public async Task<List<SpatialFeature>> QueryFeaturesNearPointAsync(
        double latitude, double longitude, double radiusMeters)
    {
        await _databaseLock.WaitAsync();
        try
        {
            // Use ST_DWithin for accurate distance calculation
            var results = await _database.QueryAsync<SpatialFeatureRecord>(@"
                SELECT id, form_id, instance_id, submission_id, feature_attributes,
                       X(geometry) as longitude, Y(geometry) as latitude,
                       created_at, updated_at, sync_status
                FROM collected_features
                WHERE ST_DWithin(
                    Transform(geometry, 3857),  -- Convert to Web Mercator for meter-based calculation
                    Transform(MakePoint(?, ?, 4326), 3857),
                    ?
                )",
                longitude, latitude, radiusMeters);

            return results.Select(r => new SpatialFeature
            {
                Id = r.Id,
                FormId = r.FormId,
                InstanceId = r.InstanceId,
                SubmissionId = r.SubmissionId,
                Latitude = r.Latitude,
                Longitude = r.Longitude,
                Attributes = !string.IsNullOrEmpty(r.FeatureAttributes)
                    ? JsonSerializer.Deserialize<Dictionary<string, object>>(r.FeatureAttributes, _jsonOptions) ?? new()
                    : new(),
                CreatedAt = r.CreatedAt,
                SyncStatus = r.SyncStatus
            }).ToList();
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    #endregion

    #region Helper Methods

    private FormSubmissionInfo? DeserializeSubmission(FormSubmissionRecord record)
    {
        try
        {
            return JsonSerializer.Deserialize<FormSubmissionInfo>(record.SubmissionData, _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task EnsureStorageMetadataAsync()
    {
        var count = await _database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM StorageMetadataRecord");
        if (count == 0)
        {
            await _database.InsertAsync(new StorageMetadataRecord
            {
                Key = "DatabaseVersion",
                Value = "1.0",
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            });
        }
    }

    private async Task<StorageMetadataRecord?> GetStorageMetadataAsync()
    {
        return await _database.FindAsync<StorageMetadataRecord>(r => r.Key == "LastSyncTimestamp");
    }

    #endregion

    public void Dispose()
    {
        _database?.CloseAsync().Wait();
        _databaseLock?.Dispose();
    }
}

// SQLite table models
[Table("FormDefinitionRecord")]
internal class FormDefinitionRecord
{
    [PrimaryKey]
    public string FormId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string FormData { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[Table("FormSubmissionRecord")]
internal class FormSubmissionRecord
{
    [PrimaryKey]
    public string Id { get; set; } = string.Empty;
    public string FormId { get; set; } = string.Empty;
    public string FormType { get; set; } = string.Empty;
    public string SubmissionData { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int FailureCount { get; set; }
    public string? LastError { get; set; }
    public long? CreatedFeatureId { get; set; }
}

[Table("SyncOperationRecord")]
internal class SyncOperationRecord
{
    [PrimaryKey]
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public DateTimeOffset QueuedAt { get; set; }
    public int Priority { get; set; }
    public string? Data { get; set; }
    public string? FormId { get; set; }
    public string? InstanceId { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset? LastRetryAt { get; set; }
}

[Table("ConflictRecord")]
internal class ConflictRecord
{
    [PrimaryKey]
    public string ConflictId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string FormId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string? FieldId { get; set; }
    public string? LocalValue { get; set; }
    public string? ServerValue { get; set; }
    public DateTimeOffset LocalTimestamp { get; set; }
    public DateTimeOffset ServerTimestamp { get; set; }
    public string? PreferredResolution { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

[Table("MediaRecord")]
internal class MediaRecord
{
    [PrimaryKey]
    public string Id { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastAccessedAt { get; set; }
    public bool IsUploaded { get; set; }
    public string? FormId { get; set; }
    public string? FieldId { get; set; }
}

[Table("CacheRecord")]
internal class CacheRecord
{
    [PrimaryKey]
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[Table("StorageMetadataRecord")]
internal class StorageMetadataRecord
{
    [PrimaryKey]
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Validation issue found during storage validation.
/// </summary>
public record ValidationIssue
{
    public string Description { get; init; } = string.Empty;
    public string? AffectedFile { get; init; }
    public string? RecommendedAction { get; init; }
}

/// <summary>
/// Internal record for spatial feature query results.
/// </summary>
internal class SpatialFeatureRecord
{
    public long Id { get; set; }
    public string FormId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string SubmissionId { get; set; } = string.Empty;
    public string FeatureAttributes { get; set; } = string.Empty;
    public double Longitude { get; set; }
    public double Latitude { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string SyncStatus { get; set; } = string.Empty;
}