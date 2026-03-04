// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using FieldDataCollection.Models;

namespace FieldDataCollection.Services;

/// <summary>
/// Local storage service for offline data management.
/// Handles forms, submissions, media, and sync state persistence.
/// </summary>
public interface ILocalStorageService
{
    #region Form Management

    /// <summary>
    /// Saves a form definition locally for offline use.
    /// </summary>
    Task SaveFormDefinitionAsync(Geospatial.V1.FormDefinition form);

    /// <summary>
    /// Retrieves a form definition from local storage.
    /// </summary>
    Task<Geospatial.V1.FormDefinition?> GetFormDefinitionAsync(string formId, string? version = null);

    /// <summary>
    /// Gets all locally stored form definitions.
    /// </summary>
    Task<List<Geospatial.V1.FormDefinition>> GetAllFormDefinitionsAsync();

    /// <summary>
    /// Deletes a form definition from local storage.
    /// </summary>
    Task DeleteFormDefinitionAsync(string formId, string? version = null);

    #endregion

    #region Form Submissions

    /// <summary>
    /// Saves a pending form submission for later upload.
    /// </summary>
    Task SavePendingSubmissionAsync(FormSubmissionInfo submission);

    /// <summary>
    /// Retrieves all pending form submissions.
    /// </summary>
    Task<List<FormSubmissionInfo>> GetPendingSubmissionsAsync();

    /// <summary>
    /// Marks a submission as completed and removes from pending list.
    /// </summary>
    Task MarkSubmissionCompletedAsync(string submissionId, long createdFeatureId);

    /// <summary>
    /// Marks a submission as failed with error details.
    /// </summary>
    Task MarkSubmissionFailedAsync(string submissionId, string error);

    /// <summary>
    /// Updates submission information.
    /// </summary>
    Task UpdateSubmissionAsync(FormSubmissionInfo submission);

    /// <summary>
    /// Gets submission history for analytics.
    /// </summary>
    Task<List<FormSubmissionInfo>> GetSubmissionHistoryAsync(int maxItems = 100);

    #endregion

    #region Sync Operations

    /// <summary>
    /// Saves a pending sync operation.
    /// </summary>
    Task SavePendingOperationAsync(SyncOperation operation);

    /// <summary>
    /// Retrieves all pending sync operations.
    /// </summary>
    Task<List<SyncOperation>> GetPendingOperationsAsync();

    /// <summary>
    /// Removes a completed sync operation.
    /// </summary>
    Task RemovePendingOperationAsync(string operationId);

    /// <summary>
    /// Gets the timestamp of the last successful sync.
    /// </summary>
    Task<DateTimeOffset?> GetLastSyncTimestampAsync();

    /// <summary>
    /// Updates the last sync timestamp.
    /// </summary>
    Task SetLastSyncTimestampAsync(DateTimeOffset timestamp);

    #endregion

    #region Conflict Management

    /// <summary>
    /// Saves conflict information for user resolution.
    /// </summary>
    Task SaveConflictAsync(ConflictInfo conflict);

    /// <summary>
    /// Retrieves all pending conflicts.
    /// </summary>
    Task<List<ConflictInfo>> GetPendingConflictsAsync();

    /// <summary>
    /// Updates conflict resolution strategy.
    /// </summary>
    Task UpdateConflictResolutionAsync(string conflictId, ConflictResolutionStrategy strategy);

    /// <summary>
    /// Applies conflict resolution and removes from pending list.
    /// </summary>
    Task ApplyConflictResolutionAsync(string conflictId, ConflictResolutionStrategy strategy);

    #endregion

    #region Media Management

    /// <summary>
    /// Saves media file locally and returns storage path.
    /// </summary>
    Task<string> SaveMediaAsync(string fileName, Stream mediaStream, string contentType);

    /// <summary>
    /// Retrieves media file stream.
    /// </summary>
    Task<Stream?> GetMediaAsync(string localPath);

    /// <summary>
    /// Gets all locally stored media files.
    /// </summary>
    Task<List<MediaInfo>> GetAllMediaAsync();

    /// <summary>
    /// Deletes media file from local storage.
    /// </summary>
    Task DeleteMediaAsync(string localPath);

    /// <summary>
    /// Cleans up orphaned media files.
    /// </summary>
    Task CleanupOrphanedMediaAsync();

    #endregion

    #region Cache Management

    /// <summary>
    /// Stores cached data with expiration.
    /// </summary>
    Task SetCacheAsync<T>(string key, T data, TimeSpan? expiration = null);

    /// <summary>
    /// Retrieves cached data.
    /// </summary>
    Task<T?> GetCacheAsync<T>(string key);

    /// <summary>
    /// Removes cached data.
    /// </summary>
    Task RemoveCacheAsync(string key);

    /// <summary>
    /// Clears expired cache entries.
    /// </summary>
    Task ClearExpiredCacheAsync();

    #endregion

    #region Storage Management

    /// <summary>
    /// Gets storage usage statistics.
    /// </summary>
    Task<StorageInfo> GetStorageInfoAsync();

    /// <summary>
    /// Performs storage cleanup to free space.
    /// </summary>
    Task<long> CleanupStorageAsync(StorageCleanupOptions options);

    /// <summary>
    /// Exports data for backup or transfer.
    /// </summary>
    Task<Stream> ExportDataAsync(DataExportOptions options);

    /// <summary>
    /// Imports data from backup.
    /// </summary>
    Task ImportDataAsync(Stream dataStream, DataImportOptions options);

    #endregion

    #region Diagnostics

    /// <summary>
    /// Gets diagnostic information about local storage health.
    /// </summary>
    Task<StorageDiagnostics> GetDiagnosticsAsync();

    /// <summary>
    /// Validates storage integrity.
    /// </summary>
    Task<StorageValidationResult> ValidateStorageAsync();

    /// <summary>
    /// Repairs corrupted storage if possible.
    /// </summary>
    Task<StorageRepairResult> RepairStorageAsync();

    #endregion

    #region Spatial Data Management

    /// <summary>
    /// Saves a spatial feature with geometry and attributes.
    /// </summary>
    Task SaveSpatialFeatureAsync(string formId, string instanceId, string submissionId,
        double latitude, double longitude, Dictionary<string, object> attributes);

    /// <summary>
    /// Queries spatial features within a bounding box.
    /// </summary>
    Task<List<SpatialFeature>> QueryFeaturesInBoundsAsync(
        double minLat, double minLon, double maxLat, double maxLon);

    /// <summary>
    /// Queries features near a point within a radius (in meters).
    /// </summary>
    Task<List<SpatialFeature>> QueryFeaturesNearPointAsync(
        double latitude, double longitude, double radiusMeters);

    #endregion
}

/// <summary>
/// Spatial feature with geometry and attributes from field data collection.
/// </summary>
public record SpatialFeature
{
    public long Id { get; init; }
    public string FormId { get; init; } = string.Empty;
    public string InstanceId { get; init; } = string.Empty;
    public string SubmissionId { get; init; } = string.Empty;
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public Dictionary<string, object> Attributes { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public string SyncStatus { get; init; } = string.Empty;
}

/// <summary>
/// Media file information.
/// </summary>
public record MediaInfo
{
    public string LocalPath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastAccessedAt { get; init; }
    public bool IsUploaded { get; init; }
    public string? FormId { get; init; }
    public string? FieldId { get; init; }
}

/// <summary>
/// Storage usage information.
/// </summary>
public record StorageInfo
{
    public long TotalUsedBytes { get; init; }
    public long FormsStorageBytes { get; init; }
    public long MediaStorageBytes { get; init; }
    public long CacheStorageBytes { get; init; }
    public long SubmissionsStorageBytes { get; init; }
    public long DatabaseSizeBytes { get; init; }
    public long AvailableSpaceBytes { get; init; }
    public int FormCount { get; init; }
    public int MediaFileCount { get; init; }
    public int PendingSubmissionCount { get; init; }
    public int ConflictCount { get; init; }
}

/// <summary>
/// Storage cleanup options.
/// </summary>
public record StorageCleanupOptions
{
    public bool DeleteOldSubmissions { get; init; } = true;
    public bool DeleteOrphanedMedia { get; init; } = true;
    public bool ClearExpiredCache { get; init; } = true;
    public bool CompactDatabase { get; init; } = false;
    public TimeSpan? OlderThan { get; init; } = TimeSpan.FromDays(30);
    public long? MaxStorageBytes { get; init; }
    public List<string> PreserveForms { get; init; } = new();
}

/// <summary>
/// Data export options.
/// </summary>
public record DataExportOptions
{
    public bool IncludeForms { get; init; } = true;
    public bool IncludeSubmissions { get; init; } = true;
    public bool IncludeMedia { get; init; } = false;
    public bool IncludeCache { get; init; } = false;
    public List<string> FormIds { get; init; } = new(); // Empty = all forms
    public DateTimeOffset? SinceDate { get; init; }
    public DataExportFormat Format { get; init; } = DataExportFormat.Json;
    public bool CompressOutput { get; init; } = true;
}

/// <summary>
/// Data import options.
/// </summary>
public record DataImportOptions
{
    public bool OverwriteExisting { get; init; } = false;
    public bool ValidateIntegrity { get; init; } = true;
    public ConflictResolutionStrategy ConflictStrategy { get; init; } = ConflictResolutionStrategy.UserChoice;
    public bool ImportMedia { get; init; } = true;
    public List<string> IncludeFormIds { get; init; } = new(); // Empty = all forms
}

/// <summary>
/// Data export formats.
/// </summary>
public enum DataExportFormat
{
    Json,
    Sqlite,
    Csv,
    GeoPackage
}

/// <summary>
/// Storage diagnostics information.
/// </summary>
public record StorageDiagnostics
{
    public StorageHealthStatus Status { get; init; }
    public List<StorageIssue> Issues { get; init; } = new();
    public StorageInfo StorageInfo { get; init; } = new();
    public DatabaseInfo DatabaseInfo { get; init; } = new();
    public List<string> RecommendedActions { get; init; } = new();
    public DateTimeOffset LastCheckTime { get; init; }
}

/// <summary>
/// Storage health status.
/// </summary>
public enum StorageHealthStatus
{
    Healthy,
    Warning,
    Critical,
    Corrupted
}

/// <summary>
/// Storage issue information.
/// </summary>
public record StorageIssue
{
    public StorageIssueType Type { get; init; }
    public string Description { get; init; } = string.Empty;
    public StorageIssueSeverity Severity { get; init; }
    public string? AffectedComponent { get; init; }
    public string? RecommendedAction { get; init; }
}

/// <summary>
/// Types of storage issues.
/// </summary>
public enum StorageIssueType
{
    LowDiskSpace,
    DatabaseCorruption,
    OrphanedFiles,
    MissingFiles,
    PermissionDenied,
    PerformanceDegradation
}

/// <summary>
/// Storage issue severity.
/// </summary>
public enum StorageIssueSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Database information.
/// </summary>
public record DatabaseInfo
{
    public string Version { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public int TableCount { get; init; }
    public int IndexCount { get; init; }
    public bool IntegrityCheckPassed { get; init; }
    public TimeSpan LastVacuumDuration { get; init; }
    public DateTimeOffset? LastVacuumTime { get; init; }
}

/// <summary>
/// Storage validation result.
/// </summary>
public record StorageValidationResult
{
    public bool IsValid { get; init; }
    public List<ValidationIssue> Issues { get; init; } = new();
    public int ValidatedFiles { get; init; }
    public int CorruptedFiles { get; init; }
    public int MissingFiles { get; init; }
    public TimeSpan ValidationDuration { get; init; }
}

/// <summary>
/// Storage repair result.
/// </summary>
public record StorageRepairResult
{
    public bool Success { get; init; }
    public List<string> RepairedIssues { get; init; } = new();
    public List<string> UnrepairedIssues { get; init; } = new();
    public int FilesRepaired { get; init; }
    public int FilesUnrecoverable { get; init; }
    public TimeSpan RepairDuration { get; init; }
}