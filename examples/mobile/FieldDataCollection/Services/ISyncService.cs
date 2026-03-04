namespace FieldDataCollection.Services;

/// <summary>
/// Service for managing data synchronization between local storage and remote server.
/// Handles offline/online state, conflict resolution, and sync progress reporting.
/// </summary>
public interface ISyncService
{
    /// <summary>
    /// Gets the current sync status.
    /// </summary>
    SyncStatus CurrentStatus { get; }

    /// <summary>
    /// Gets whether the app is currently online.
    /// </summary>
    bool IsOnline { get; }

    /// <summary>
    /// Gets the last successful sync time.
    /// </summary>
    DateTimeOffset? LastSyncTime { get; }

    /// <summary>
    /// Gets the number of pending local changes.
    /// </summary>
    int PendingChangesCount { get; }

    /// <summary>
    /// Performs a full synchronization with the remote server.
    /// </summary>
    /// <returns>Result of the sync operation.</returns>
    Task<SyncResult> PerformSyncAsync();

    /// <summary>
    /// Performs a full synchronization with progress reporting.
    /// </summary>
    /// <param name="progress">Progress reporter for sync updates.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>Result of the sync operation.</returns>
    Task<SyncResult> PerformSyncAsync(IProgress<SyncProgress> progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enables or disables automatic background synchronization.
    /// </summary>
    /// <param name="enabled">True to enable automatic sync.</param>
    Task SetAutoSyncEnabledAsync(bool enabled);

    /// <summary>
    /// Forces a refresh of the online status.
    /// </summary>
    Task RefreshOnlineStatusAsync();

    /// <summary>
    /// Event raised when sync status changes.
    /// </summary>
    event EventHandler<SyncStatusChangedEventArgs>? SyncStatusChanged;

    /// <summary>
    /// Event raised when online status changes.
    /// </summary>
    event EventHandler<OnlineStatusChangedEventArgs>? OnlineStatusChanged;
}

/// <summary>
/// Enumeration of possible sync states.
/// </summary>
public enum SyncStatus
{
    Idle,
    Downloading,
    Uploading,
    Processing,
    Completed,
    Failed
}

/// <summary>
/// Result of a sync operation.
/// </summary>
public record SyncResult
{
    public bool IsSuccess { get; init; }
    public int DownloadedChanges { get; init; }
    public int UploadedChanges { get; init; }
    public int ConflictsResolved { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Progress information for sync operations.
/// </summary>
public record SyncProgress
{
    public string CurrentOperation { get; init; } = string.Empty;
    public int CompletedItems { get; init; }
    public int TotalItems { get; init; }
    public double PercentComplete => TotalItems > 0 ? (double)CompletedItems / TotalItems * 100 : 0;
}

/// <summary>
/// Event arguments for sync status changes.
/// </summary>
public class SyncStatusChangedEventArgs : EventArgs
{
    public SyncStatus Status { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// Event arguments for online status changes.
/// </summary>
public class OnlineStatusChangedEventArgs : EventArgs
{
    public bool IsOnline { get; init; }
}