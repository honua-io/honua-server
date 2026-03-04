// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace FieldDataCollection.Models;

/// <summary>
/// Comprehensive sync result information.
/// </summary>
public record SyncResult
{
    public SyncStatus Status { get; set; } = SyncStatus.InProgress;
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public TimeSpan Duration => EndTime - StartTime;
    public int DownloadedItems { get; set; }
    public int UploadedItems { get; set; }
    public int ConflictCount { get; set; }
    public List<string> Errors { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();

    public bool HasErrors => Errors.Any();
    public bool IsSuccess => Status == SyncStatus.Success;
}

/// <summary>
/// Sync operation status enumeration.
/// </summary>
public enum SyncStatus
{
    InProgress,
    Success,
    PartialSuccess,
    Failed,
    Cancelled,
    Skipped
}

/// <summary>
/// Sync options configuration.
/// </summary>
public record SyncOptions
{
    public bool ForceSync { get; init; } = false;
    public SyncDirection SyncDirection { get; init; } = SyncDirection.Bidirectional;
    public bool SyncOnlyWifi { get; init; } = false;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
    public List<string> IncludeForms { get; init; } = new();
    public List<string> ExcludeForms { get; init; } = new();
    public bool SkipMedia { get; init; } = false;
    public ConflictResolutionStrategy DefaultConflictResolution { get; init; } = ConflictResolutionStrategy.ServerWins;
}

/// <summary>
/// Sync direction enumeration.
/// </summary>
[Flags]
public enum SyncDirection
{
    Upload = 1,
    Download = 2,
    Bidirectional = Upload | Download
}

/// <summary>
/// Individual sync operation for queuing.
/// </summary>
public record SyncOperation
{
    public string Id { get; set; } = string.Empty;
    public SyncOperationType Type { get; set; }
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? LastRetryAt { get; set; }
    public int RetryCount { get; set; }
    public int Priority { get; set; } = 5; // 1-10, lower is higher priority
    public Dictionary<string, object>? Data { get; set; }
    public string? FormId { get; set; }
    public string? InstanceId { get; set; }
}

/// <summary>
/// Types of sync operations.
/// </summary>
public enum SyncOperationType
{
    FormSubmission,
    FeatureEdit,
    MediaUpload,
    ConflictResolution,
    FormDownload,
    DataDownload
}

/// <summary>
/// Form submission information for sync tracking.
/// </summary>
public record FormSubmissionInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FormId { get; set; } = string.Empty;
    public FormType FormType { get; set; }
    public Geospatial.V1.FormInstance? GrpcInstance { get; set; }
    public XFormInstance? XFormInstance { get; set; }
    public List<FormAttachment> Attachments { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public FormSubmissionStatus Status { get; set; } = FormSubmissionStatus.Pending;
    public int FailureCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public long? CreatedFeatureId { get; set; }
}

/// <summary>
/// Form submission status.
/// </summary>
public enum FormSubmissionStatus
{
    Pending,
    Uploading,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Conflict information for resolution.
/// </summary>
public record ConflictInfo
{
    public string ConflictId { get; set; } = Guid.NewGuid().ToString("N");
    public ConflictType Type { get; set; }
    public string FormId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string? FieldId { get; set; }
    public object? LocalValue { get; set; }
    public object? ServerValue { get; set; }
    public DateTimeOffset LocalTimestamp { get; set; }
    public DateTimeOffset ServerTimestamp { get; set; }
    public ConflictResolutionStrategy? PreferredResolution { get; set; }
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Types of conflicts that can occur.
/// </summary>
public enum ConflictType
{
    FieldValueConflict,
    FormVersionConflict,
    DeletionConflict,
    PermissionConflict,
    SchemaConflict
}

/// <summary>
/// Conflict resolution strategies.
/// </summary>
public enum ConflictResolutionStrategy
{
    LocalWins,
    ServerWins,
    MergeValues,
    UserChoice,
    CreateCopy,
    LastWriteWins
}

/// <summary>
/// Network connection information.
/// </summary>
public record NetworkInfo
{
    public bool IsConnected { get; init; }
    public NetworkType NetworkType { get; init; }
    public ConnectionQuality Quality { get; init; }
    public bool IsMetered { get; init; }
    public double? BandwidthMbps { get; init; }
    public TimeSpan? Latency { get; init; }
}

/// <summary>
/// Connection quality levels.
/// </summary>
public enum ConnectionQuality
{
    Poor,      // <1 Mbps
    Fair,      // 1-5 Mbps
    Good,      // 5-20 Mbps
    Excellent  // >20 Mbps
}

/// <summary>
/// Device performance information.
/// </summary>
public record DevicePerformanceInfo
{
    public double BatteryLevel { get; init; }
    public bool IsLowPowerMode { get; init; }
    public long AvailableStorageMB { get; init; }
    public double MemoryUsagePercent { get; init; }
    public bool IsThermallyThrottled { get; init; }
    public ProcessorUsage ProcessorUsage { get; init; }
}

/// <summary>
/// Processor usage information.
/// </summary>
public record ProcessorUsage
{
    public double OverallPercent { get; init; }
    public double AppPercent { get; init; }
    public int CoreCount { get; init; }
}

// Event argument classes
public class SyncProgressEventArgs : EventArgs
{
    public int ProgressPercent { get; }
    public string Message { get; }
    public SyncPhase Phase { get; }
    public Dictionary<string, object> Details { get; }

    public SyncProgressEventArgs(int progressPercent, string message, SyncPhase phase = SyncPhase.Unknown)
    {
        ProgressPercent = Math.Clamp(progressPercent, 0, 100);
        Message = message;
        Phase = phase;
        Details = new Dictionary<string, object>();
    }
}

public class SyncCompletedEventArgs : EventArgs
{
    public SyncResult Result { get; }

    public SyncCompletedEventArgs(SyncResult result)
    {
        Result = result;
    }
}

public class ConflictDetectedEventArgs : EventArgs
{
    public ConflictInfo Conflict { get; }

    public ConflictDetectedEventArgs(ConflictInfo conflict)
    {
        Conflict = conflict;
    }
}

/// <summary>
/// Sync phases for progress tracking.
/// </summary>
public enum SyncPhase
{
    Unknown,
    Initializing,
    DownloadingForms,
    DownloadingData,
    UploadingData,
    UploadingMedia,
    ResolvingConflicts,
    Finalizing,
    Completed
}

/// <summary>
/// Sync statistics for monitoring and analytics.
/// </summary>
public record SyncStatistics
{
    public int TotalSyncs { get; init; }
    public int SuccessfulSyncs { get; init; }
    public int FailedSyncs { get; init; }
    public TimeSpan AverageSyncDuration { get; init; }
    public long TotalDataDownloaded { get; init; }
    public long TotalDataUploaded { get; init; }
    public int TotalConflictsResolved { get; init; }
    public DateTimeOffset LastSyncTime { get; init; }
    public DateTimeOffset? LastSuccessfulSync { get; init; }
    public NetworkInfo? LastNetworkInfo { get; init; }
    public Dictionary<SyncOperationType, int> OperationCounts { get; init; } = new();
}

/// <summary>
/// Sync health information for diagnostics.
/// </summary>
public record SyncHealth
{
    public SyncHealthStatus Status { get; init; }
    public List<SyncHealthIssue> Issues { get; init; } = new();
    public SyncStatistics Statistics { get; init; } = new();
    public DateTimeOffset LastCheckTime { get; init; }
    public TimeSpan? TimeSinceLastSuccess { get; init; }
    public int PendingOperationCount { get; init; }
    public long StorageUsageMB { get; init; }
}

/// <summary>
/// Sync health status levels.
/// </summary>
public enum SyncHealthStatus
{
    Healthy,
    Warning,
    Critical,
    Unknown
}

/// <summary>
/// Individual sync health issue.
/// </summary>
public record SyncHealthIssue
{
    public SyncHealthIssueType Type { get; init; }
    public string Description { get; init; } = string.Empty;
    public SyncHealthSeverity Severity { get; init; }
    public DateTimeOffset DetectedAt { get; init; }
    public string? RecommendedAction { get; init; }
    public Dictionary<string, object> Details { get; init; } = new();
}

/// <summary>
/// Types of sync health issues.
/// </summary>
public enum SyncHealthIssueType
{
    NetworkConnectivity,
    StorageSpace,
    Authentication,
    ServerError,
    ConflictBacklog,
    PerformanceDegradation,
    DataCorruption
}

/// <summary>
/// Severity levels for health issues.
/// </summary>
public enum SyncHealthSeverity
{
    Info,
    Warning,
    Error,
    Critical
}