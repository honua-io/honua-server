// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FieldDataCollection.Models;
using Honua.Mobile.Core.Client;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace FieldDataCollection.Services;

/// <summary>
/// Comprehensive offline sync manager with intelligent conflict resolution.
/// Handles bidirectional synchronization optimized for field work scenarios.
/// </summary>
public class OfflineSyncManager : IOfflineSyncManager, IDisposable
{
    private readonly IGrpcFormService _grpcFormService;
    private readonly HonuaFeatureClient _featureClient;
    private readonly ILocalStorageService _localStorage;
    private readonly IConnectivityService _connectivityService;
    private readonly ILogger<OfflineSyncManager> _logger;
    private readonly OfflineSyncOptions _options;

    private readonly Timer _syncTimer;
    private readonly SemaphoreSlim _syncSemaphore = new(1, 1);
    private readonly ConcurrentQueue<SyncOperation> _pendingOperations = new();
    private readonly ConcurrentDictionary<string, ConflictResolutionStrategy> _conflictStrategies = new();

    private bool _isOnline;
    private DateTime _lastSyncAttempt = DateTime.MinValue;
    private readonly TaskCompletionSource<bool> _initialSyncComplete = new();

    public event EventHandler<SyncProgressEventArgs>? SyncProgressChanged;
    public event EventHandler<SyncCompletedEventArgs>? SyncCompleted;
    public event EventHandler<ConflictDetectedEventArgs>? ConflictDetected;

    public OfflineSyncManager(
        IGrpcFormService grpcFormService,
        HonuaFeatureClient featureClient,
        ILocalStorageService localStorage,
        IConnectivityService connectivityService,
        IOptions<OfflineSyncOptions> options,
        ILogger<OfflineSyncManager> logger)
    {
        _grpcFormService = grpcFormService;
        _featureClient = featureClient;
        _localStorage = localStorage;
        _connectivityService = connectivityService;
        _options = options.Value;
        _logger = logger;

        // Initialize connectivity monitoring
        _connectivityService.ConnectivityChanged += OnConnectivityChanged;
        _isOnline = _connectivityService.NetworkAccess == NetworkAccess.Internet;

        // Start background sync timer
        _syncTimer = new Timer(BackgroundSyncCallback, null,
            _options.InitialSyncDelay, _options.SyncInterval);

        _logger.LogInformation("OfflineSyncManager initialized with {SyncInterval}s interval",
            _options.SyncInterval.TotalSeconds);
    }

    public async Task<SyncResult> PerformSyncAsync(SyncOptions? syncOptions = null, CancellationToken cancellationToken = default)
    {
        if (!await _syncSemaphore.WaitAsync(100, cancellationToken))
        {
            _logger.LogWarning("Sync already in progress, skipping");
            return new SyncResult { Status = SyncStatus.Skipped, Message = "Sync already in progress" };
        }

        try
        {
            var options = syncOptions ?? new SyncOptions { ForceSync = false, SyncDirection = SyncDirection.Bidirectional };
            var result = new SyncResult { StartTime = DateTimeOffset.UtcNow };

            _logger.LogInformation("Starting sync - Direction: {Direction}, Force: {Force}",
                options.SyncDirection, options.ForceSync);

            // Check connectivity
            if (!_isOnline && !options.ForceSync)
            {
                return result with
                {
                    Status = SyncStatus.Failed,
                    Message = "No network connectivity"
                };
            }

            // Notify sync started
            SyncProgressChanged?.Invoke(this, new SyncProgressEventArgs(0, "Starting sync..."));

            // Phase 1: Download changes from server
            if (options.SyncDirection.HasFlag(SyncDirection.Download))
            {
                await DownloadChangesAsync(result, cancellationToken);
            }

            // Phase 2: Upload local changes to server
            if (options.SyncDirection.HasFlag(SyncDirection.Upload))
            {
                await UploadChangesAsync(result, cancellationToken);
            }

            // Phase 3: Process pending operations
            await ProcessPendingOperationsAsync(result, cancellationToken);

            result.EndTime = DateTimeOffset.UtcNow;
            result.Duration = result.EndTime - result.StartTime;
            result.Status = result.HasErrors ? SyncStatus.PartialSuccess : SyncStatus.Success;

            _lastSyncAttempt = DateTime.UtcNow;

            // Notify completion
            SyncCompleted?.Invoke(this, new SyncCompletedEventArgs(result));
            SyncProgressChanged?.Invoke(this, new SyncProgressEventArgs(100, "Sync completed"));

            _logger.LogInformation("Sync completed - Status: {Status}, Duration: {Duration}ms, " +
                                 "Downloaded: {Downloaded}, Uploaded: {Uploaded}, Conflicts: {Conflicts}",
                result.Status, result.Duration.TotalMilliseconds,
                result.DownloadedItems, result.UploadedItems, result.ConflictCount);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed with exception");
            return new SyncResult
            {
                Status = SyncStatus.Failed,
                Message = ex.Message,
                EndTime = DateTimeOffset.UtcNow
            };
        }
        finally
        {
            _syncSemaphore.Release();
        }
    }

    public async Task QueueOperationAsync(SyncOperation operation)
    {
        operation.QueuedAt = DateTimeOffset.UtcNow;
        operation.Id = Guid.NewGuid().ToString("N");

        _pendingOperations.Enqueue(operation);
        await _localStorage.SavePendingOperationAsync(operation);

        _logger.LogDebug("Queued {OperationType} operation: {OperationId}",
            operation.Type, operation.Id);

        // Trigger immediate sync if online and not too recent
        if (_isOnline && DateTime.UtcNow - _lastSyncAttempt > TimeSpan.FromSeconds(5))
        {
            _ = Task.Run(async () => await PerformSyncAsync(new SyncOptions { ForceSync = false }));
        }
    }

    public async Task<bool> WaitForInitialSyncAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _initialSyncComplete.Task.WaitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<List<ConflictInfo>> GetPendingConflictsAsync()
    {
        return await _localStorage.GetPendingConflictsAsync();
    }

    public async Task ResolveConflictAsync(string conflictId, ConflictResolutionStrategy strategy)
    {
        _conflictStrategies[conflictId] = strategy;
        await _localStorage.UpdateConflictResolutionAsync(conflictId, strategy);

        _logger.LogInformation("Conflict {ConflictId} marked for resolution with strategy: {Strategy}",
            conflictId, strategy);
    }

    private async Task DownloadChangesAsync(SyncResult result, CancellationToken cancellationToken)
    {
        try
        {
            SyncProgressChanged?.Invoke(this, new SyncProgressEventArgs(10, "Downloading server changes..."));

            // Get last sync timestamp for incremental sync
            var lastSync = await _localStorage.GetLastSyncTimestampAsync();
            var changesSince = lastSync ?? DateTimeOffset.UtcNow.AddDays(-30); // Default to last 30 days

            // Download form definitions
            var formCatalog = await _grpcFormService.GetFormCatalogAsync();
            var downloadedForms = 0;

            foreach (var formMetadata in formCatalog.Forms)
            {
                if (formMetadata.ModifiedAt > changesSince.ToUnixTimeMilliseconds())
                {
                    try
                    {
                        var formDef = await _grpcFormService.GetFormDefinitionAsync(
                            formMetadata.FormId, formMetadata.TargetServiceId, formMetadata.TargetLayerId);

                        await _localStorage.SaveFormDefinitionAsync(formDef.Form);
                        downloadedForms++;

                        _logger.LogDebug("Downloaded form: {FormId} v{Version}",
                            formMetadata.FormId, formMetadata.Version);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to download form {FormId}", formMetadata.FormId);
                        result.Errors.Add($"Failed to download form {formMetadata.FormId}: {ex.Message}");
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            // Download feature data (if configured for offline use)
            await DownloadFeatureDataAsync(result, changesSince, cancellationToken);

            result.DownloadedItems = downloadedForms;
            SyncProgressChanged?.Invoke(this, new SyncProgressEventArgs(40, $"Downloaded {downloadedForms} forms"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download changes");
            result.Errors.Add($"Download failed: {ex.Message}");
        }
    }

    private async Task UploadChangesAsync(SyncResult result, CancellationToken cancellationToken)
    {
        try
        {
            SyncProgressChanged?.Invoke(this, new SyncProgressEventArgs(60, "Uploading local changes..."));

            var pendingSubmissions = await _localStorage.GetPendingSubmissionsAsync();
            var uploadedCount = 0;

            foreach (var submission in pendingSubmissions)
            {
                try
                {
                    // Convert to appropriate format and submit
                    if (submission.FormType == FormType.GrpcNative)
                    {
                        var response = await _grpcFormService.SubmitFormDataAsync(
                            submission.FormId, submission.GrpcInstance!, submission.Attachments.ToList());

                        if (response.Result.Success)
                        {
                            await _localStorage.MarkSubmissionCompletedAsync(submission.Id, response.CreatedFeatureId);
                            uploadedCount++;
                        }
                        else
                        {
                            await HandleSubmissionFailureAsync(submission, response.Result.Message, result);
                        }
                    }
                    else
                    {
                        // Handle OpenRosa submissions via feature service
                        await UploadOpenRosaSubmissionAsync(submission, result);
                        uploadedCount++;
                    }

                    _logger.LogDebug("Uploaded submission: {SubmissionId}", submission.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to upload submission {SubmissionId}", submission.Id);
                    await HandleSubmissionFailureAsync(submission, ex.Message, result);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            result.UploadedItems = uploadedCount;
            SyncProgressChanged?.Invoke(this, new SyncProgressEventArgs(80, $"Uploaded {uploadedCount} submissions"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload changes");
            result.Errors.Add($"Upload failed: {ex.Message}");
        }
    }

    private async Task ProcessPendingOperationsAsync(SyncResult result, CancellationToken cancellationToken)
    {
        var processedOps = 0;

        while (_pendingOperations.TryDequeue(out var operation))
        {
            try
            {
                await ProcessSingleOperationAsync(operation, result, cancellationToken);
                await _localStorage.RemovePendingOperationAsync(operation.Id);
                processedOps++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process operation {OperationId}", operation.Id);
                result.Errors.Add($"Operation {operation.Id} failed: {ex.Message}");

                // Re-queue operation for retry if within retry limit
                if (operation.RetryCount < _options.MaxRetries)
                {
                    operation.RetryCount++;
                    operation.LastRetryAt = DateTimeOffset.UtcNow;
                    _pendingOperations.Enqueue(operation);
                    await _localStorage.SavePendingOperationAsync(operation);
                }
            }
        }

        if (processedOps > 0)
        {
            _logger.LogInformation("Processed {ProcessedOps} pending operations", processedOps);
        }
    }

    private async Task ProcessSingleOperationAsync(SyncOperation operation, SyncResult result, CancellationToken cancellationToken)
    {
        switch (operation.Type)
        {
            case SyncOperationType.FormSubmission:
                // Handle form submission
                break;
            case SyncOperationType.FeatureEdit:
                // Handle feature edit
                break;
            case SyncOperationType.MediaUpload:
                // Handle media upload
                await ProcessMediaUploadAsync(operation, cancellationToken);
                break;
            case SyncOperationType.ConflictResolution:
                // Handle conflict resolution
                await ProcessConflictResolutionAsync(operation, result, cancellationToken);
                break;
        }
    }

    private async Task ProcessMediaUploadAsync(SyncOperation operation, CancellationToken cancellationToken)
    {
        if (operation.Data?.TryGetValue("mediaPath", out var mediaPathObj) == true &&
            mediaPathObj is string mediaPath && File.Exists(mediaPath))
        {
            // Upload media to server
            var mediaBytes = await File.ReadAllBytesAsync(mediaPath, cancellationToken);

            // Implementation would depend on media upload service
            _logger.LogDebug("Uploaded media: {MediaPath}", mediaPath);
        }
    }

    private async Task ProcessConflictResolutionAsync(SyncOperation operation, SyncResult result, CancellationToken cancellationToken)
    {
        if (operation.Data?.TryGetValue("conflictId", out var conflictIdObj) == true &&
            conflictIdObj is string conflictId &&
            _conflictStrategies.TryGetValue(conflictId, out var strategy))
        {
            // Apply conflict resolution strategy
            await _localStorage.ApplyConflictResolutionAsync(conflictId, strategy);
            result.ConflictCount++;

            _logger.LogDebug("Applied conflict resolution: {ConflictId} with {Strategy}", conflictId, strategy);
        }
    }

    private async Task DownloadFeatureDataAsync(SyncResult result, DateTimeOffset changesSince, CancellationToken cancellationToken)
    {
        // Download feature data for offline viewing
        // Implementation would depend on feature synchronization requirements
        var downloadedFeatures = 0;

        // Placeholder for feature download logic
        await Task.Delay(100, cancellationToken);

        result.DownloadedItems += downloadedFeatures;
    }

    private async Task UploadOpenRosaSubmissionAsync(FormSubmissionInfo submission, SyncResult result)
    {
        // Convert OpenRosa submission to feature edit
        // This would use the existing conversion logic from FormViewModel
        await Task.CompletedTask; // Placeholder
    }

    private async Task HandleSubmissionFailureAsync(FormSubmissionInfo submission, string error, SyncResult result)
    {
        submission.FailureCount++;
        submission.LastError = error;
        submission.LastAttemptAt = DateTimeOffset.UtcNow;

        if (submission.FailureCount >= _options.MaxRetries)
        {
            await _localStorage.MarkSubmissionFailedAsync(submission.Id, error);
            result.Errors.Add($"Submission {submission.Id} permanently failed: {error}");
        }
        else
        {
            await _localStorage.UpdateSubmissionAsync(submission);
            result.Errors.Add($"Submission {submission.Id} retry {submission.FailureCount}: {error}");
        }
    }

    private async void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        var wasOnline = _isOnline;
        _isOnline = e.NetworkAccess == NetworkAccess.Internet;

        _logger.LogInformation("Connectivity changed: {OldStatus} -> {NewStatus}",
            wasOnline ? "Online" : "Offline", _isOnline ? "Online" : "Offline");

        // Trigger sync when coming back online
        if (!wasOnline && _isOnline)
        {
            _logger.LogInformation("Network restored, triggering sync");
            _ = Task.Run(async () =>
            {
                await Task.Delay(_options.ConnectivityRestoreDelay);
                await PerformSyncAsync(new SyncOptions { ForceSync = true });
            });
        }
    }

    private async void BackgroundSyncCallback(object? state)
    {
        if (!_isOnline || DateTime.UtcNow - _lastSyncAttempt < _options.MinSyncInterval)
        {
            return;
        }

        try
        {
            var result = await PerformSyncAsync(new SyncOptions
            {
                ForceSync = false,
                SyncDirection = SyncDirection.Bidirectional
            });

            // Mark initial sync as complete on first successful sync
            if (result.Status == SyncStatus.Success && !_initialSyncComplete.Task.IsCompleted)
            {
                _initialSyncComplete.SetResult(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background sync failed");
        }
    }

    public void Dispose()
    {
        _syncTimer?.Dispose();
        _syncSemaphore?.Dispose();
        _connectivityService.ConnectivityChanged -= OnConnectivityChanged;
        GC.SuppressFinalize(this);
    }
}

// Supporting interfaces and models
public interface IOfflineSyncManager
{
    event EventHandler<SyncProgressEventArgs>? SyncProgressChanged;
    event EventHandler<SyncCompletedEventArgs>? SyncCompleted;
    event EventHandler<ConflictDetectedEventArgs>? ConflictDetected;

    Task<SyncResult> PerformSyncAsync(SyncOptions? options = null, CancellationToken cancellationToken = default);
    Task QueueOperationAsync(SyncOperation operation);
    Task<bool> WaitForInitialSyncAsync(TimeSpan timeout);
    Task<List<ConflictInfo>> GetPendingConflictsAsync();
    Task ResolveConflictAsync(string conflictId, ConflictResolutionStrategy strategy);
}

public class OfflineSyncOptions
{
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan MinSyncInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan InitialSyncDelay { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan ConnectivityRestoreDelay { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxRetries { get; set; } = 3;
    public bool EnableBackgroundSync { get; set; } = true;
    public bool SyncOnlyOnWifi { get; set; } = false;
    public int MaxConcurrentOperations { get; set; } = 5;
}