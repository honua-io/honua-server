// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Proto = Honua.Server.Features.Grpc.Proto.V2;

namespace Honua.Server.Features.Infrastructure.Services;

/// <summary>
/// Service for managing bidirectional synchronization sessions between clients and server.
/// Handles conflict resolution, change tracking, and offline sync scenarios.
/// </summary>
public interface ISyncService
{
    /// <summary>
    /// Creates a new synchronization session.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Sync session for managing bidirectional communication.</returns>
    Task<ISyncSession> CreateSyncSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes a sync request and returns appropriate response.
    /// </summary>
    /// <param name="request">Sync request from client.</param>
    /// <param name="session">Active sync session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Sync response.</returns>
    Task<Proto.SyncResponse> ProcessSyncRequestAsync(
        Proto.SyncRequest request,
        ISyncSession session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects conflicts between client and server changes.
    /// </summary>
    /// <param name="clientChanges">Changes from client.</param>
    /// <param name="serverGeneration">Current server generation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of detected conflicts.</returns>
    Task<IEnumerable<Proto.FeatureConflict>> DetectConflictsAsync(
        IEnumerable<Proto.FeatureChange> clientChanges,
        long serverGeneration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies conflict resolutions to resolve sync conflicts.
    /// </summary>
    /// <param name="resolutions">Conflict resolution decisions.</param>
    /// <param name="session">Active sync session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Results of conflict resolution.</returns>
    Task<Proto.ConflictResolutionResult> ApplyConflictResolutionsAsync(
        IEnumerable<Proto.ConflictResolution> resolutions,
        ISyncSession session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets server changes since specified generation for pull synchronization.
    /// </summary>
    /// <param name="serviceId">Service identifier.</param>
    /// <param name="layerId">Layer identifier.</param>
    /// <param name="sinceGeneration">Client's last known generation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Server changes since generation.</returns>
    Task<IEnumerable<Proto.FeatureChange>> GetServerChangesSinceAsync(
        string serviceId,
        int layerId,
        long sinceGeneration,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents an active synchronization session between client and server.
/// </summary>
public interface ISyncSession : IAsyncDisposable
{
    /// <summary>
    /// Unique identifier for this sync session.
    /// </summary>
    string SessionId { get; }

    /// <summary>
    /// Current state of the sync session.
    /// </summary>
    SyncSessionState State { get; }

    /// <summary>
    /// Number of changes applied during this session.
    /// </summary>
    int ChangesApplied { get; }

    /// <summary>
    /// Number of conflicts resolved during this session.
    /// </summary>
    int ConflictsResolved { get; }

    /// <summary>
    /// When the session was started.
    /// </summary>
    DateTime StartTime { get; }

    /// <summary>
    /// Initializes the sync session with metadata from client.
    /// </summary>
    /// <param name="metadata">Sync metadata from client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InitializeAsync(Proto.SyncMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes feature changes from client.
    /// </summary>
    /// <param name="changes">Feature changes to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Results of change processing.</returns>
    Task<Proto.ChangeProcessingResult> ProcessChangesAsync(
        IEnumerable<Proto.FeatureChange> changes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records conflict resolutions for this session.
    /// </summary>
    /// <param name="resolutions">Conflict resolution decisions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordConflictResolutionsAsync(
        IEnumerable<Proto.ConflictResolution> resolutions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes the sync session and returns final generation.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Final server generation after sync completion.</returns>
    Task<long> GetFinalGenerationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets sync session statistics.
    /// </summary>
    /// <returns>Session performance and activity statistics.</returns>
    SyncSessionStatistics GetStatistics();
}

/// <summary>
/// State of a synchronization session.
/// </summary>
public enum SyncSessionState
{
    Created,
    Initialized,
    ProcessingChanges,
    ResolvingConflicts,
    Completing,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Statistics for a sync session.
/// </summary>
public class SyncSessionStatistics
{
    public int FeaturesAdded { get; init; }
    public int FeaturesUpdated { get; init; }
    public int FeaturesDeleted { get; init; }
    public int ConflictsDetected { get; init; }
    public int ConflictsResolved { get; init; }
    public int ConflictsPending { get; init; }
    public TimeSpan Duration { get; init; }
    public long BytesTransferred { get; init; }
    public double CompressionRatio { get; init; }

    public int TotalChanges => FeaturesAdded + FeaturesUpdated + FeaturesDeleted;
    public bool HasUnresolvedConflicts => ConflictsPending > 0;
}