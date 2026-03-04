using FieldDataCollection.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace FieldDataCollection.ViewModels;

/// <summary>
/// View model for the Sync Center screen.
/// Manages data synchronization status, manual sync operations, and conflict resolution.
/// </summary>
public class SyncCenterViewModel : BaseViewModel
{
    private readonly ISyncService _syncService;
    private readonly IDialogService _dialogService;
    private readonly IAppSettingsService _settings;

    private SyncStatus _syncStatus = SyncStatus.Idle;
    private bool _isOnline;
    private DateTimeOffset? _lastSyncTime;
    private int _pendingChanges;
    private bool _autoSyncEnabled;
    private int _syncIntervalMinutes;
    private string _syncProgress = string.Empty;

    public SyncCenterViewModel(
        ISyncService syncService,
        IDialogService dialogService,
        IAppSettingsService settings)
    {
        _syncService = syncService;
        _dialogService = dialogService;
        _settings = settings;

        Title = "Sync Center";

        SyncHistory = new ObservableCollection<SyncHistoryItem>();

        // Initialize commands
        ManualSyncCommand = new Command(async () => await PerformManualSyncAsync(), () => CanSync);
        ToggleAutoSyncCommand = new Command(async () => await ToggleAutoSyncAsync());
        RefreshStatusCommand = new Command(async () => await RefreshStatusAsync());
        ViewSyncHistoryCommand = new Command(async () => await ViewSyncHistoryAsync());

        // Subscribe to sync service events
        _syncService.SyncStatusChanged += OnSyncStatusChanged;
        _syncService.OnlineStatusChanged += OnOnlineStatusChanged;

        // Initialize status
        _ = Task.Run(InitializeAsync);
    }

    #region Properties

    public ObservableCollection<SyncHistoryItem> SyncHistory { get; }

    public SyncStatus SyncStatus
    {
        get => _syncStatus;
        set
        {
            SetProperty(ref _syncStatus, value);
            OnPropertyChanged(nameof(SyncStatusText));
            OnPropertyChanged(nameof(CanSync));
            ((Command)ManualSyncCommand).ChangeCanExecute();
        }
    }

    public string SyncStatusText => SyncStatus switch
    {
        SyncStatus.Idle => "Ready to sync",
        SyncStatus.Downloading => "Downloading changes...",
        SyncStatus.Uploading => "Uploading changes...",
        SyncStatus.Processing => "Processing data...",
        SyncStatus.Completed => "Sync completed",
        SyncStatus.Failed => "Sync failed",
        _ => "Unknown status"
    };

    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            SetProperty(ref _isOnline, value);
            OnPropertyChanged(nameof(OnlineStatusText));
            OnPropertyChanged(nameof(CanSync));
            ((Command)ManualSyncCommand).ChangeCanExecute();
        }
    }

    public string OnlineStatusText => IsOnline ? "Online" : "Offline";

    public DateTimeOffset? LastSyncTime
    {
        get => _lastSyncTime;
        set
        {
            SetProperty(ref _lastSyncTime, value);
            OnPropertyChanged(nameof(LastSyncTimeText));
        }
    }

    public string LastSyncTimeText => LastSyncTime?.ToString("MMM dd, yyyy h:mm tt") ?? "Never";

    public int PendingChanges
    {
        get => _pendingChanges;
        set
        {
            SetProperty(ref _pendingChanges, value);
            OnPropertyChanged(nameof(PendingChangesText));
        }
    }

    public string PendingChangesText => PendingChanges == 0 ? "No pending changes" : $"{PendingChanges} pending changes";

    public bool AutoSyncEnabled
    {
        get => _autoSyncEnabled;
        set => SetProperty(ref _autoSyncEnabled, value);
    }

    public int SyncIntervalMinutes
    {
        get => _syncIntervalMinutes;
        set => SetProperty(ref _syncIntervalMinutes, value);
    }

    public string SyncProgress
    {
        get => _syncProgress;
        set => SetProperty(ref _syncProgress, value);
    }

    public bool CanSync => IsOnline && SyncStatus == SyncStatus.Idle && !IsBusy;

    #endregion

    #region Commands

    public ICommand ManualSyncCommand { get; }
    public ICommand ToggleAutoSyncCommand { get; }
    public ICommand RefreshStatusCommand { get; }
    public ICommand ViewSyncHistoryCommand { get; }

    #endregion

    #region Private Methods

    private async Task InitializeAsync()
    {
        await RefreshStatusAsync();
        AutoSyncEnabled = _settings.IsAutoSyncEnabled;
        SyncIntervalMinutes = _settings.SyncIntervalMinutes;

        // Load sync history (mock data for now)
        LoadSyncHistory();
    }

    private async Task RefreshStatusAsync()
    {
        SyncStatus = _syncService.CurrentStatus;
        IsOnline = _syncService.IsOnline;
        LastSyncTime = _syncService.LastSyncTime;
        PendingChanges = _syncService.PendingChangesCount;

        await _syncService.RefreshOnlineStatusAsync();
    }

    private async Task PerformManualSyncAsync()
    {
        if (!CanSync) return;

        await ExecuteAsync(async () =>
        {
            var progress = new Progress<SyncProgress>(p => SyncProgress = p.CurrentOperation);
            var result = await _syncService.PerformSyncAsync(progress);

            var resultMessage = result.IsSuccess
                ? $"Sync completed successfully!\n\nDownloaded: {result.DownloadedChanges} changes\nUploaded: {result.UploadedChanges} changes\nDuration: {result.Duration.TotalSeconds:F1} seconds"
                : $"Sync failed: {result.ErrorMessage}";

            await _dialogService.DisplayAlertAsync(
                result.IsSuccess ? "Sync Complete" : "Sync Failed",
                resultMessage);

            // Add to sync history
            SyncHistory.Insert(0, new SyncHistoryItem
            {
                Timestamp = DateTime.Now,
                IsSuccess = result.IsSuccess,
                DownloadedChanges = result.DownloadedChanges,
                UploadedChanges = result.UploadedChanges,
                Duration = result.Duration,
                ErrorMessage = result.ErrorMessage
            });

            // Refresh status
            await RefreshStatusAsync();

        }, OnError);
    }

    private async Task ToggleAutoSyncAsync()
    {
        AutoSyncEnabled = !AutoSyncEnabled;
        await _settings.SetAutoSyncEnabledAsync(AutoSyncEnabled);
        await _syncService.SetAutoSyncEnabledAsync(AutoSyncEnabled);

        var statusMessage = AutoSyncEnabled ? "Auto-sync enabled" : "Auto-sync disabled";
        await _dialogService.ShowToastAsync(statusMessage);
    }

    private async Task ViewSyncHistoryAsync()
    {
        if (!SyncHistory.Any())
        {
            await _dialogService.DisplayAlertAsync("No History", "No sync operations have been performed yet.");
            return;
        }

        var latestSync = SyncHistory.First();
        var historyMessage = $"Latest sync: {latestSync.Timestamp:MMM dd, yyyy h:mm tt}\n" +
                           $"Status: {(latestSync.IsSuccess ? "Success" : "Failed")}\n" +
                           $"Downloaded: {latestSync.DownloadedChanges} changes\n" +
                           $"Uploaded: {latestSync.UploadedChanges} changes\n" +
                           $"Duration: {latestSync.Duration.TotalSeconds:F1} seconds";

        if (!string.IsNullOrEmpty(latestSync.ErrorMessage))
        {
            historyMessage += $"\nError: {latestSync.ErrorMessage}";
        }

        await _dialogService.DisplayAlertAsync("Sync History", historyMessage);
    }

    private void LoadSyncHistory()
    {
        // Mock sync history data
        SyncHistory.Add(new SyncHistoryItem
        {
            Timestamp = DateTime.Now.AddHours(-2),
            IsSuccess = true,
            DownloadedChanges = 5,
            UploadedChanges = 2,
            Duration = TimeSpan.FromSeconds(3.2)
        });

        SyncHistory.Add(new SyncHistoryItem
        {
            Timestamp = DateTime.Now.AddHours(-6),
            IsSuccess = true,
            DownloadedChanges = 12,
            UploadedChanges = 0,
            Duration = TimeSpan.FromSeconds(5.8)
        });

        SyncHistory.Add(new SyncHistoryItem
        {
            Timestamp = DateTime.Now.AddDays(-1),
            IsSuccess = false,
            DownloadedChanges = 0,
            UploadedChanges = 0,
            Duration = TimeSpan.FromSeconds(15.0),
            ErrorMessage = "Network connection failed"
        });
    }

    private void OnSyncStatusChanged(object? sender, SyncStatusChangedEventArgs e)
    {
        SyncStatus = e.Status;
        SyncProgress = e.Message ?? string.Empty;
    }

    private void OnOnlineStatusChanged(object? sender, OnlineStatusChangedEventArgs e)
    {
        IsOnline = e.IsOnline;
    }

    private async void OnError(Exception ex)
    {
        await _dialogService.DisplayAlertAsync("Error", ex.Message);
    }

    #endregion
}

/// <summary>
/// Represents an item in the sync history.
/// </summary>
public class SyncHistoryItem
{
    public DateTime Timestamp { get; set; }
    public bool IsSuccess { get; set; }
    public int DownloadedChanges { get; set; }
    public int UploadedChanges { get; set; }
    public TimeSpan Duration { get; set; }
    public string? ErrorMessage { get; set; }

    public string StatusIcon => IsSuccess ? "✅" : "❌";
    public string Summary => $"{StatusIcon} {Timestamp:MMM dd h:mm tt} - {(IsSuccess ? "Success" : "Failed")}";
}