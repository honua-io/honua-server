using Honua.Mobile.Core.Client;

namespace FieldDataCollection.Services;

/// <summary>
/// Implementation of ISyncService coordinating between local storage and remote server.
/// Currently provides basic sync capabilities with future offline storage integration.
/// </summary>
public class SyncService : ISyncService
{
    private readonly HonuaFeatureClient _client;
    private readonly IAppSettingsService _settings;
    private SyncStatus _currentStatus = SyncStatus.Idle;
    private DateTimeOffset? _lastSyncTime;
    private bool _isOnline = true;

    public event EventHandler<SyncStatusChangedEventArgs>? SyncStatusChanged;
    public event EventHandler<OnlineStatusChangedEventArgs>? OnlineStatusChanged;

    public SyncService(HonuaFeatureClient client, IAppSettingsService settings)
    {
        _client = client;
        _settings = settings;

        // Monitor connectivity changes
        Connectivity.ConnectivityChanged += OnConnectivityChanged;
        RefreshOnlineStatusAsync().ConfigureAwait(false);
    }

    public SyncStatus CurrentStatus
    {
        get => _currentStatus;
        private set
        {
            if (_currentStatus != value)
            {
                _currentStatus = value;
                SyncStatusChanged?.Invoke(this, new SyncStatusChangedEventArgs { Status = value });
            }
        }
    }

    public bool IsOnline
    {
        get => _isOnline;
        private set
        {
            if (_isOnline != value)
            {
                _isOnline = value;
                OnlineStatusChanged?.Invoke(this, new OnlineStatusChangedEventArgs { IsOnline = value });
            }
        }
    }

    public DateTimeOffset? LastSyncTime => _lastSyncTime;

    // For now, return 0 pending changes - will integrate with offline storage
    public int PendingChangesCount => 0;

    public async Task<SyncResult> PerformSyncAsync()
    {
        var progress = new Progress<SyncProgress>();
        return await PerformSyncAsync(progress, CancellationToken.None);
    }

    public async Task<SyncResult> PerformSyncAsync(IProgress<SyncProgress> progress, CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.Now;

        try
        {
            CurrentStatus = SyncStatus.Processing;
            progress.Report(new SyncProgress { CurrentOperation = "Checking connectivity..." });

            if (!IsOnline)
            {
                return new SyncResult
                {
                    IsSuccess = false,
                    ErrorMessage = "No internet connection available",
                    Duration = DateTime.Now - startTime
                };
            }

            progress.Report(new SyncProgress { CurrentOperation = "Connecting to server..." });

            // Test server connectivity with a simple query
            // This validates authentication and server availability
            try
            {
                await _client.CountAsync("test-service", 0, new());
            }
            catch (Exception ex)
            {
                return new SyncResult
                {
                    IsSuccess = false,
                    ErrorMessage = $"Server connection failed: {ex.Message}",
                    Duration = DateTime.Now - startTime
                };
            }

            // Future: Implement actual sync logic
            // - Download changes from server
            // - Upload local changes
            // - Resolve conflicts
            progress.Report(new SyncProgress
            {
                CurrentOperation = "Sync completed",
                CompletedItems = 1,
                TotalItems = 1
            });

            CurrentStatus = SyncStatus.Completed;
            _lastSyncTime = DateTimeOffset.Now;

            return new SyncResult
            {
                IsSuccess = true,
                DownloadedChanges = 0,
                UploadedChanges = 0,
                ConflictsResolved = 0,
                Duration = DateTime.Now - startTime
            };
        }
        catch (OperationCanceledException)
        {
            CurrentStatus = SyncStatus.Failed;
            return new SyncResult
            {
                IsSuccess = false,
                ErrorMessage = "Sync was cancelled",
                Duration = DateTime.Now - startTime
            };
        }
        catch (Exception ex)
        {
            CurrentStatus = SyncStatus.Failed;
            return new SyncResult
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                Duration = DateTime.Now - startTime
            };
        }
        finally
        {
            if (CurrentStatus != SyncStatus.Failed)
            {
                CurrentStatus = SyncStatus.Idle;
            }
        }
    }

    public Task SetAutoSyncEnabledAsync(bool enabled)
    {
        return _settings.SetAutoSyncEnabledAsync(enabled);
    }

    public async Task RefreshOnlineStatusAsync()
    {
        var networkAccess = Connectivity.NetworkAccess;
        IsOnline = networkAccess == NetworkAccess.Internet;
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        IsOnline = e.NetworkAccess == NetworkAccess.Internet;
    }
}