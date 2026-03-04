// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using FieldDataCollection.Models;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FieldDataCollection.Services;

/// <summary>
/// Main client service for Honua field data collection.
/// Coordinates gRPC communication, offline storage, and synchronization.
/// </summary>
public class HonuaMobileClient : IDisposable
{
    private readonly GrpcChannel _grpcChannel;
    private readonly Proto.FeatureService.FeatureServiceClient _featureClient;
    private readonly Proto.FormService.FormServiceClient _formClient;
    private readonly ILocalStorageService _localStorage;
    private readonly IOfflineSyncManager _syncManager;
    private readonly ILogger<HonuaMobileClient> _logger;
    private readonly HonuaMobileClientOptions _options;

    public HonuaMobileClient(
        ILocalStorageService localStorage,
        IOfflineSyncManager syncManager,
        IOptions<HonuaMobileClientOptions> options,
        ILogger<HonuaMobileClient> logger)
    {
        _localStorage = localStorage;
        _syncManager = syncManager;
        _logger = logger;
        _options = options.Value;

        // Create gRPC channel
        var channelOptions = new GrpcChannelOptions
        {
            HttpHandler = CreateHttpHandler(),
            MaxReceiveMessageSize = _options.MaxReceiveMessageSize,
            MaxSendMessageSize = _options.MaxSendMessageSize,
            DisposeHttpClient = true
        };

        _grpcChannel = GrpcChannel.ForAddress(_options.ServerUrl, channelOptions);
        _featureClient = new Proto.FeatureService.FeatureServiceClient(_grpcChannel);
        _formClient = new Proto.FormService.FormServiceClient(_grpcChannel);

        // Start background sync if enabled
        if (_options.EnableBackgroundSync)
        {
            _ = Task.Run(BackgroundSyncLoop);
        }
    }

    #region Form Management

    /// <summary>
    /// Downloads and caches a form definition for offline use.
    /// </summary>
    public async Task<Geospatial.V1.FormDefinition?> GetFormDefinitionAsync(string formId, bool forceRefresh = false)
    {
        try
        {
            // Check local cache first unless forcing refresh
            if (!forceRefresh)
            {
                var cached = await _localStorage.GetFormDefinitionAsync(formId);
                if (cached != null)
                {
                    _logger.LogDebug("Retrieved form {FormId} from local cache", formId);
                    return cached;
                }
            }

            // Fetch from server
            var request = new Proto.GetFormDefinitionRequest { FormId = formId };
            var headers = await CreateAuthHeaders();

            var response = await _formClient.GetFormDefinitionAsync(request, headers);

            // Cache locally
            await _localStorage.SaveFormDefinitionAsync(response.Form);

            _logger.LogInformation("Downloaded and cached form {FormId}", formId);
            return response.Form;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get form definition {FormId}", formId);

            // Fall back to cached version if available
            var cached = await _localStorage.GetFormDefinitionAsync(formId);
            if (cached != null)
            {
                _logger.LogWarning("Using cached version of form {FormId} due to network error", formId);
                return cached;
            }

            throw;
        }
    }

    /// <summary>
    /// Gets all locally available form definitions.
    /// </summary>
    public async Task<List<Geospatial.V1.FormDefinition>> GetAvailableFormsAsync()
    {
        return await _localStorage.GetAllFormDefinitionsAsync();
    }

    #endregion

    #region Form Submission

    /// <summary>
    /// Submits form data, handling offline scenarios gracefully.
    /// </summary>
    public async Task<SubmissionResult> SubmitFormDataAsync(FormSubmissionInfo submission)
    {
        try
        {
            // Always save to local storage first for offline capability
            await _localStorage.SavePendingSubmissionAsync(submission);

            // Try immediate submission if online
            if (await IsOnlineAsync())
            {
                var success = await TrySubmitToServerAsync(submission);
                if (success)
                {
                    await _localStorage.MarkSubmissionCompletedAsync(submission.Id, 0); // TODO: Get actual feature ID
                    return new SubmissionResult { Success = true, IsOffline = false };
                }
            }

            // Queue for later sync
            _logger.LogInformation("Queued submission {SubmissionId} for offline sync", submission.Id);
            return new SubmissionResult { Success = true, IsOffline = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit form data {SubmissionId}", submission.Id);
            await _localStorage.MarkSubmissionFailedAsync(submission.Id, ex.Message);
            return new SubmissionResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Saves a spatial feature with location data.
    /// </summary>
    public async Task SaveSpatialFeatureAsync(string formId, string instanceId, string submissionId,
        double latitude, double longitude, Dictionary<string, object> attributes)
    {
        await _localStorage.SaveSpatialFeatureAsync(formId, instanceId, submissionId, latitude, longitude, attributes);
        _logger.LogDebug("Saved spatial feature for submission {SubmissionId} at {Lat}, {Lon}",
            submissionId, latitude, longitude);
    }

    #endregion

    #region Spatial Queries

    /// <summary>
    /// Queries features in a geographic bounding box.
    /// </summary>
    public async Task<List<SpatialFeature>> QueryFeaturesInAreaAsync(
        double minLat, double minLon, double maxLat, double maxLon)
    {
        return await _localStorage.QueryFeaturesInBoundsAsync(minLat, minLon, maxLat, maxLon);
    }

    /// <summary>
    /// Finds features near a specific location.
    /// </summary>
    public async Task<List<SpatialFeature>> FindNearbyFeaturesAsync(
        double latitude, double longitude, double radiusMeters = 100)
    {
        return await _localStorage.QueryFeaturesNearPointAsync(latitude, longitude, radiusMeters);
    }

    #endregion

    #region Synchronization

    /// <summary>
    /// Performs manual sync of all pending data.
    /// </summary>
    public async Task<SyncResult> SyncNowAsync(SyncOptions? options = null)
    {
        try
        {
            _logger.LogInformation("Starting manual sync");
            var result = await _syncManager.PerformSyncAsync(options);
            _logger.LogInformation("Manual sync completed: {Status}, {UploadedItems} uploaded, {DownloadedItems} downloaded",
                result.Status, result.UploadedItems, result.DownloadedItems);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual sync failed");
            return new SyncResult
            {
                Status = SyncStatus.Failed,
                Message = ex.Message,
                EndTime = DateTimeOffset.Now
            };
        }
    }

    /// <summary>
    /// Gets sync status and pending operations.
    /// </summary>
    public async Task<SyncHealth> GetSyncHealthAsync()
    {
        var pendingSubmissions = await _localStorage.GetPendingSubmissionsAsync();
        var pendingOperations = await _localStorage.GetPendingOperationsAsync();
        var lastSync = await _localStorage.GetLastSyncTimestampAsync();

        var issues = new List<SyncHealthIssue>();

        // Check for old pending submissions
        var oldSubmissions = pendingSubmissions.Where(s => s.CreatedAt < DateTimeOffset.Now.AddHours(-24)).ToList();
        if (oldSubmissions.Any())
        {
            issues.Add(new SyncHealthIssue
            {
                Type = SyncHealthIssueType.ConflictBacklog,
                Description = $"{oldSubmissions.Count} submissions pending for over 24 hours",
                Severity = SyncHealthSeverity.Warning,
                RecommendedAction = "Check network connectivity and perform manual sync"
            });
        }

        // Check network connectivity
        if (!await IsOnlineAsync())
        {
            issues.Add(new SyncHealthIssue
            {
                Type = SyncHealthIssueType.NetworkConnectivity,
                Description = "No network connectivity available",
                Severity = SyncHealthSeverity.Error,
                RecommendedAction = "Connect to Wi-Fi or cellular network"
            });
        }

        var status = issues.Any(i => i.Severity == SyncHealthSeverity.Critical) ? SyncHealthStatus.Critical :
                    issues.Any(i => i.Severity == SyncHealthSeverity.Error) ? SyncHealthStatus.Warning :
                    issues.Any() ? SyncHealthStatus.Warning : SyncHealthStatus.Healthy;

        return new SyncHealth
        {
            Status = status,
            Issues = issues,
            LastCheckTime = DateTimeOffset.Now,
            PendingOperationCount = pendingOperations.Count,
            TimeSinceLastSuccess = lastSync.HasValue ? DateTimeOffset.Now - lastSync.Value : null
        };
    }

    #endregion

    #region Media Management

    /// <summary>
    /// Saves media file locally with optional compression.
    /// </summary>
    public async Task<string> SaveMediaAsync(string fileName, Stream mediaStream, string contentType)
    {
        // TODO: Implement media compression if enabled in options
        return await _localStorage.SaveMediaAsync(fileName, mediaStream, contentType);
    }

    /// <summary>
    /// Retrieves locally stored media file.
    /// </summary>
    public async Task<Stream?> GetMediaAsync(string localPath)
    {
        return await _localStorage.GetMediaAsync(localPath);
    }

    #endregion

    #region Storage Management

    /// <summary>
    /// Gets storage usage information.
    /// </summary>
    public async Task<StorageInfo> GetStorageInfoAsync()
    {
        return await _localStorage.GetStorageInfoAsync();
    }

    /// <summary>
    /// Performs storage cleanup to free space.
    /// </summary>
    public async Task<long> CleanupStorageAsync(StorageCleanupOptions? options = null)
    {
        options ??= new StorageCleanupOptions
        {
            DeleteOldSubmissions = true,
            DeleteOrphanedMedia = true,
            ClearExpiredCache = true,
            OlderThan = TimeSpan.FromDays(_options.RetainDataDays)
        };

        return await _localStorage.CleanupStorageAsync(options);
    }

    #endregion

    #region Private Methods

    private async Task<bool> TrySubmitToServerAsync(FormSubmissionInfo submission)
    {
        try
        {
            var request = new Proto.SubmitFormDataRequest
            {
                FormId = submission.FormId,
                InstanceId = submission.Id // Using submission ID as instance ID
                // TODO: Convert submission data to proto format
            };

            var headers = await CreateAuthHeaders();
            var response = await _formClient.SubmitFormDataAsync(request, headers);

            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to submit to server: {Message}", ex.Message);
            return false;
        }
    }

    private async Task<bool> IsOnlineAsync()
    {
        try
        {
            // Simple connectivity check - ping the server
            var request = new Proto.GetFormMetadataRequest { FormId = "connectivity_test" };
            var headers = await CreateAuthHeaders();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _formClient.GetFormMetadataAsync(request, headers, cancellationToken: cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<Grpc.Core.Metadata> CreateAuthHeaders()
    {
        var headers = new Grpc.Core.Metadata();

        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            headers.Add("X-API-Key", _options.ApiKey);
        }

        // TODO: Add OIDC token if configured

        return await Task.FromResult(headers);
    }

    private HttpMessageHandler CreateHttpHandler()
    {
        var handler = new HttpClientHandler();

        // Configure for mobile scenarios
        if (_options.AcceptInvalidCertificates)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        return handler;
    }

    private async Task BackgroundSyncLoop()
    {
        while (!_disposalCancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.SyncIntervalMinutes * 60 * 1000, _disposalCancellation.Token);

                if (await IsOnlineAsync())
                {
                    _logger.LogDebug("Starting background sync");
                    await _syncManager.PerformSyncAsync();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background sync failed");
            }
        }
    }

    #endregion

    #region Disposal

    private readonly CancellationTokenSource _disposalCancellation = new();

    public void Dispose()
    {
        _disposalCancellation.Cancel();
        _grpcChannel?.Dispose();
        _disposalCancellation.Dispose();
    }

    #endregion
}

/// <summary>
/// Configuration options for the mobile client.
/// </summary>
public class HonuaMobileClientOptions
{
    public const string SectionName = "HonuaMobileClient";

    /// <summary>
    /// Server URL for gRPC connections.
    /// </summary>
    public string ServerUrl { get; set; } = "https://localhost:5001";

    /// <summary>
    /// API key for authentication.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Whether to enable background synchronization.
    /// </summary>
    public bool EnableBackgroundSync { get; set; } = true;

    /// <summary>
    /// Background sync interval in minutes.
    /// </summary>
    public int SyncIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// Number of days to retain completed data locally.
    /// </summary>
    public int RetainDataDays { get; set; } = 30;

    /// <summary>
    /// Maximum gRPC receive message size in bytes.
    /// </summary>
    public int MaxReceiveMessageSize { get; set; } = 16 * 1024 * 1024; // 16MB

    /// <summary>
    /// Maximum gRPC send message size in bytes.
    /// </summary>
    public int MaxSendMessageSize { get; set; } = 16 * 1024 * 1024; // 16MB

    /// <summary>
    /// Whether to accept invalid SSL certificates (for testing).
    /// </summary>
    public bool AcceptInvalidCertificates { get; set; } = false;
}

/// <summary>
/// Result of a form submission operation.
/// </summary>
public record SubmissionResult
{
    public bool Success { get; init; }
    public bool IsOffline { get; init; }
    public string? Error { get; init; }
    public long? CreatedFeatureId { get; init; }
}