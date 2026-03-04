// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using Honua.Mobile.Core.Models;

namespace Honua.Mobile.Core.Client;

/// <summary>
/// Enhanced feature client interface with v2 protocol support.
/// Provides improved error handling, multiple geometry encodings, and mobile optimizations.
/// </summary>
public interface IFeatureClientV2 : IDisposable
{
    #region Query Operations

    /// <summary>
    /// Executes a feature query with enhanced filtering and mobile optimizations.
    /// </summary>
    /// <param name="serviceId">Service identifier.</param>
    /// <param name="layerId">Layer identifier.</param>
    /// <param name="request">Enhanced query request with v2 features.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with metadata and structured errors.</returns>
    Task<EnhancedQueryResult<Feature>> QueryAsync(
        string serviceId,
        int layerId,
        EnhancedFeatureQuery request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams query results with progress reporting and mobile optimizations.
    /// </summary>
    /// <param name="serviceId">Service identifier.</param>
    /// <param name="layerId">Layer identifier.</param>
    /// <param name="request">Enhanced query request.</param>
    /// <param name="progress">Progress reporting callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Async enumerable of features with metadata.</returns>
    IAsyncEnumerable<EnhancedFeature> QueryStreamAsync(
        string serviceId,
        int layerId,
        EnhancedFeatureQuery request,
        IProgress<QueryProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets count of features matching query without returning data.
    /// </summary>
    Task<long> CountAsync(
        string serviceId,
        int layerId,
        EnhancedFeatureQuery request,
        CancellationToken cancellationToken = default);

    #endregion

    #region Edit Operations

    /// <summary>
    /// Applies feature edits with enhanced conflict resolution and error reporting.
    /// </summary>
    /// <param name="serviceId">Service identifier.</param>
    /// <param name="layerId">Layer identifier.</param>
    /// <param name="request">Enhanced edit request with v2 features.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Edit result with detailed success/failure information.</returns>
    Task<EnhancedEditResult> ApplyEditsAsync(
        string serviceId,
        int layerId,
        EnhancedEditRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies large edit batches using streaming for performance.
    /// </summary>
    /// <param name="serviceId">Service identifier.</param>
    /// <param name="layerId">Layer identifier.</param>
    /// <param name="editBatches">Stream of edit batches.</param>
    /// <param name="progress">Progress reporting callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stream of edit results.</returns>
    IAsyncEnumerable<EnhancedEditResult> ApplyEditsStreamAsync(
        string serviceId,
        int layerId,
        IAsyncEnumerable<EditBatch> editBatches,
        IProgress<EditProgress>? progress = null,
        CancellationToken cancellationToken = default);

    #endregion

    #region Sync Operations

    /// <summary>
    /// Performs bidirectional synchronization with conflict resolution.
    /// </summary>
    /// <param name="syncRequest">Sync configuration and client state.</param>
    /// <param name="progress">Progress reporting callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Sync result with conflict information.</returns>
    Task<SyncResult> SynchronizeAsync(
        SyncRequest syncRequest,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts bidirectional sync session for complex scenarios.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Sync session for manual control.</returns>
    ISyncSession StartSyncSession(CancellationToken cancellationToken = default);

    #endregion

    #region Metadata Operations

    /// <summary>
    /// Gets service and layer metadata for optimization.
    /// </summary>
    /// <param name="serviceId">Service identifier.</param>
    /// <param name="layerIds">Layer identifiers (optional).</param>
    /// <param name="includeSchema">Include field definitions.</param>
    /// <param name="includeCapabilities">Include capability information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Service metadata with optimization hints.</returns>
    Task<ServiceMetadata> GetServiceMetadataAsync(
        string serviceId,
        IEnumerable<int>? layerIds = null,
        bool includeSchema = true,
        bool includeCapabilities = true,
        CancellationToken cancellationToken = default);

    #endregion

    #region Configuration

    /// <summary>
    /// Gets or sets the preferred geometry encoding for responses.
    /// </summary>
    GeometryEncoding GeometryEncoding { get; set; }

    /// <summary>
    /// Gets or sets mobile optimization settings.
    /// </summary>
    MobileOptimizations MobileOptimizations { get; set; }

    /// <summary>
    /// Gets or sets default spatial reference for queries.
    /// </summary>
    SpatialReference? DefaultSpatialReference { get; set; }

    #endregion

    #region Events

    /// <summary>
    /// Raised when sync conflicts are detected and require resolution.
    /// </summary>
    event EventHandler<ConflictDetectedEventArgs>? ConflictDetected;

    /// <summary>
    /// Raised when network connectivity changes.
    /// </summary>
    event EventHandler<ConnectivityChangedEventArgs>? ConnectivityChanged;

    /// <summary>
    /// Raised when client performance metrics are updated.
    /// </summary>
    event EventHandler<PerformanceMetricsEventArgs>? PerformanceMetricsUpdated;

    #endregion
}

/// <summary>
/// Sync session interface for complex bidirectional synchronization scenarios.
/// </summary>
public interface ISyncSession : IDisposable
{
    /// <summary>
    /// Sends sync metadata to initialize session.
    /// </summary>
    Task SendSyncMetadataAsync(SyncMetadata metadata);

    /// <summary>
    /// Sends feature changes to server.
    /// </summary>
    Task SendFeatureChangesAsync(IEnumerable<FeatureChange> changes);

    /// <summary>
    /// Sends conflict resolution decisions.
    /// </summary>
    Task SendConflictResolutionAsync(IEnumerable<ConflictResolution> resolutions);

    /// <summary>
    /// Completes the sync session.
    /// </summary>
    Task CompleteSyncAsync();

    /// <summary>
    /// Receives responses from server.
    /// </summary>
    IAsyncEnumerable<SyncResponse> ReceiveResponsesAsync();

    /// <summary>
    /// Current session state.
    /// </summary>
    SyncSessionState State { get; }
}

public enum SyncSessionState
{
    Initializing,
    Active,
    ConflictResolution,
    Completing,
    Completed,
    Failed
}

/// <summary>
/// Enhanced feature query with v2 protocol features.
/// </summary>
public class EnhancedFeatureQuery
{
    public QueryFilter? Filter { get; set; }
    public IEnumerable<long>? ObjectIds { get; set; }
    public IEnumerable<string>? OutFields { get; set; }
    public bool ReturnGeometry { get; set; } = true;
    public GeometryEncoding GeometryEncoding { get; set; } = GeometryEncoding.Structured;
    public SpatialReference? OutputSpatialReference { get; set; }
    public int? Offset { get; set; }
    public int? Limit { get; set; }
    public string? OrderBy { get; set; }
    public bool Distinct { get; set; }
    public bool CountOnly { get; set; }
    public bool IdsOnly { get; set; }
    public bool ExtentOnly { get; set; }
    public IEnumerable<StatisticDefinition>? Statistics { get; set; }
    public IEnumerable<string>? GroupBy { get; set; }
    public int? GeometryPrecision { get; set; }
    public double? MaxAllowableOffset { get; set; }
    public LevelOfDetail? LevelOfDetail { get; set; }
    public MobileOptimizations? MobileOptimizations { get; set; }
}

/// <summary>
/// Enhanced edit request with v2 protocol features.
/// </summary>
public class EnhancedEditRequest
{
    public IEnumerable<Feature>? Adds { get; set; }
    public IEnumerable<Feature>? Updates { get; set; }
    public IEnumerable<long>? Deletes { get; set; }
    public bool RollbackOnFailure { get; set; } = true;
    public bool ForceWrite { get; set; }
    public ConflictResolutionStrategy ConflictStrategy { get; set; } = ConflictResolutionStrategy.Fail;
    public bool ValidateGeometry { get; set; } = true;
    public EditMetadata? Metadata { get; set; }
}

/// <summary>
/// Enhanced query result with metadata and structured errors.
/// </summary>
public class EnhancedQueryResult<T>
{
    public string ObjectIdFieldName { get; set; } = string.Empty;
    public GeometryType GeometryType { get; set; }
    public SpatialReference? SpatialReference { get; set; }
    public IEnumerable<FieldDefinition> Fields { get; set; } = Array.Empty<FieldDefinition>();
    public IEnumerable<T> Items { get; set; } = Array.Empty<T>();
    public bool ExceededTransferLimit { get; set; }
    public long? Count { get; set; }
    public IEnumerable<long>? ObjectIds { get; set; }
    public Extent? Extent { get; set; }
    public QueryMetadata? Metadata { get; set; }
    public StructuredError? Error { get; set; }
    public bool IsSuccess => Error == null;
}

/// <summary>
/// Enhanced edit result with detailed error information.
/// </summary>
public class EnhancedEditResult
{
    public IEnumerable<EditResult> AddResults { get; set; } = Array.Empty<EditResult>();
    public IEnumerable<EditResult> UpdateResults { get; set; } = Array.Empty<EditResult>();
    public IEnumerable<EditResult> DeleteResults { get; set; } = Array.Empty<EditResult>();
    public EditSummary? Summary { get; set; }
    public StructuredError? Error { get; set; }
    public bool IsSuccess => Error == null;
}

/// <summary>
/// Enhanced feature with metadata support.
/// </summary>
public class EnhancedFeature : Feature
{
    public FeatureMetadata? Metadata { get; set; }
}

/// <summary>
/// Progress information for query operations.
/// </summary>
public class QueryProgress
{
    public int ItemsReceived { get; set; }
    public int? TotalItems { get; set; }
    public double? PercentComplete => TotalItems.HasValue && TotalItems > 0
        ? (double)ItemsReceived / TotalItems.Value * 100
        : null;
    public string CurrentOperation { get; set; } = string.Empty;
    public TimeSpan Elapsed { get; set; }
    public QueryMetadata? Metadata { get; set; }
}

/// <summary>
/// Progress information for edit operations.
/// </summary>
public class EditProgress
{
    public int EditsProcessed { get; set; }
    public int TotalEdits { get; set; }
    public int SuccessfulEdits { get; set; }
    public int FailedEdits { get; set; }
    public int ConflictsDetected { get; set; }
    public double PercentComplete => TotalEdits > 0 ? (double)EditsProcessed / TotalEdits * 100 : 0;
    public string CurrentOperation { get; set; } = string.Empty;
    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// Event arguments for conflict detection.
/// </summary>
public class ConflictDetectedEventArgs : EventArgs
{
    public IEnumerable<FeatureConflict> Conflicts { get; set; } = Array.Empty<FeatureConflict>();
    public ConflictResolutionCallback? ResolutionCallback { get; set; }
}

public delegate Task ConflictResolutionCallback(IEnumerable<ConflictResolution> resolutions);

/// <summary>
/// Event arguments for connectivity changes.
/// </summary>
public class ConnectivityChangedEventArgs : EventArgs
{
    public bool IsOnline { get; set; }
    public NetworkQuality NetworkQuality { get; set; }
    public DateTime Timestamp { get; set; }
}

public enum NetworkQuality
{
    Unknown,
    Poor,
    Moderate,
    Good,
    Excellent
}

/// <summary>
/// Event arguments for performance metrics updates.
/// </summary>
public class PerformanceMetricsEventArgs : EventArgs
{
    public PerformanceMetrics Metrics { get; set; } = new();
}

public class PerformanceMetrics
{
    public TimeSpan AverageQueryTime { get; set; }
    public TimeSpan AverageEditTime { get; set; }
    public long BytesTransferred { get; set; }
    public double CompressionRatio { get; set; }
    public int CacheHitRate { get; set; }
    public double BatteryUsageEstimate { get; set; }
    public DateTime LastUpdated { get; set; }
}