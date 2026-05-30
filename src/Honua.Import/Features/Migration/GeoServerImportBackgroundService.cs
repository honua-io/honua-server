// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Configuration;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;

namespace Honua.Migration;

/// <summary>
/// Background worker that processes queued GeoServer import jobs.
/// </summary>
internal sealed partial class GeoServerImportBackgroundService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GeoServerImportJobManager _jobManager;
    private readonly ILogger<GeoServerImportBackgroundService> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _leaderCheckInterval = TimeSpan.FromSeconds(10);

    public GeoServerImportBackgroundService(
        IConfiguration configuration,
        IHostEnvironment hostEnvironment,
        IServiceScopeFactory scopeFactory,
        GeoServerImportJobManager jobManager,
        ILogger<GeoServerImportBackgroundService> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _hostEnvironment = hostEnvironment ?? throw new ArgumentNullException(nameof(hostEnvironment));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _jobManager = jobManager ?? throw new ArgumentNullException(nameof(jobManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        => await ImportBackgroundServiceCoordinator.RunAsync(
            _jobManager,
            _logger,
            _pollInterval,
            _leaderCheckInterval,
            ProcessJobAsync,
            Log.ServiceStarting,
            Log.ServiceStopped,
            Log.NotLeader,
            Log.ProcessingError,
            stoppingToken).ConfigureAwait(false);

    private async Task ProcessJobAsync(string jobId, CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        Log.JobStarted(_logger, jobId);

        GeoServerImportRequest? request = null;
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        CancellationTokenSource? monitorCancellation = null;
        Task? monitorTask = null;
        var acknowledgeCompletion = true;
        var leadershipLost = 0;
        using var progressController = new ImportJobProgressController<GeoServerImportProgress>(
            jobId,
            _jobManager.ProgressStore,
            progressTtl: TimeSpan.FromHours(24),
            finalProgressTtl: TimeSpan.FromHours(24));

        try
        {
            var progress = await _jobManager.ProgressStore.GetProgressAsync(jobId, stoppingToken).ConfigureAwait(false);
            progressController.Seed(progress);
            if (progress != null && IsTerminalStatus(progress.Status))
            {
                await _jobManager.RequestStore.DeleteProgressAsync(jobId, stoppingToken).ConfigureAwait(false);
                if (progress.Status == GeoServerImportStatus.Cancelled)
                {
                    Log.JobCancelled(_logger, jobId, stopwatch.Elapsed.TotalSeconds);
                }

                return;
            }

            request = await _jobManager.RequestStore.GetProgressAsync(jobId, stoppingToken).ConfigureAwait(false);
            if (request == null)
            {
                Log.JobRequestNotFound(_logger, jobId);

                if (progress == null ||
                    progress.Status is GeoServerImportStatus.Completed or GeoServerImportStatus.Failed or GeoServerImportStatus.Cancelled)
                {
                    return;
                }

                var failedProgress = progress with
                {
                    Status = GeoServerImportStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = "Import request not found.",
                    CurrentPhase = "Import request missing"
                };

                await progressController.SetFinalProgressAsync(failedProgress, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(request.JobId))
            {
                request = request with { JobId = jobId };
            }

            var allowUnsafeLocalUrls = GeoServerImportExecutionSettings.ShouldAllowUnsafeLocalUrls(_hostEnvironment, _configuration);
            var urlValidation = await GeoServerServiceUrlValidation.ValidateAsync(
                request.GeoServerRestUrl,
                allowUnsafeLocalUrls,
                stoppingToken).ConfigureAwait(false);
            if (!urlValidation.IsValid)
            {
                var failedProgress = (progress ?? GeoServerImportProgress.CreateInitial(jobId, request.GeoServerRestUrl, request.TargetHonuaUrl)) with
                {
                    Status = GeoServerImportStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = urlValidation.ErrorMessage,
                    CurrentPhase = "Import blocked by GeoServer URL validation"
                };

                await progressController.SetFinalProgressAsync(failedProgress, CancellationToken.None).ConfigureAwait(false);
                await _jobManager.RequestStore.DeleteProgressAsync(jobId, stoppingToken).ConfigureAwait(false);
                Log.JobRejectedUnsafeServiceUrl(_logger, jobId, request.GeoServerRestUrl);
                return;
            }

            monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            monitorTask = ImportBackgroundServiceCoordinator.MonitorCancellationAsync(
                jobId,
                _jobManager,
                jobCancellation,
                current => current.Status == GeoServerImportStatus.Cancelled,
                () => Interlocked.Exchange(ref leadershipLost, 1),
                _logger,
                Log.LeadershipLostDuringJob,
                Log.CancellationMonitorPollFailed,
                monitorCancellation.Token);

            await using var scope = _scopeFactory.CreateAsyncScope();
            request = await ResolveSecretReferencesAsync(request, scope.ServiceProvider, jobCancellation.Token).ConfigureAwait(false);
            var importService = scope.ServiceProvider.GetRequiredService<IGeoServerImportService>();
            var progressReporter = new Progress<GeoServerImportProgress>(p =>
                _ = progressController.TryReportProgressAsync(p, _logger, Log.ProgressUpdateFailed, CancellationToken.None));

            var result = await importService.ImportConfigurationAsync(request, progressReporter, jobCancellation.Token).ConfigureAwait(false);

            stopwatch.Stop();

            // If the job was cancelled (e.g. via the cancel endpoint, which writes
            // Cancelled directly to the progress store) while the import was
            // completing, the cancellation wins: don't overwrite the terminal
            // Cancelled status with a Completed/Failed result. Imports that honour
            // the cancellation token throw and land in the catch below; this guard
            // also covers imports that run to completion without observing it.
            var storedProgress = await _jobManager.ProgressStore.GetProgressAsync(jobId, stoppingToken).ConfigureAwait(false);
            if (storedProgress?.Status == GeoServerImportStatus.Cancelled)
            {
                await _jobManager.RequestStore.DeleteProgressAsync(jobId, stoppingToken).ConfigureAwait(false);
                Log.JobCancelled(_logger, jobId, stopwatch.Elapsed.TotalSeconds);
                return;
            }

            var currentProgress = progressController.CurrentProgress ?? GeoServerImportProgress.CreateInitial(jobId, request.GeoServerRestUrl, request.TargetHonuaUrl);
            var finalProgress = currentProgress with
            {
                Status = result.Success ? GeoServerImportStatus.Completed : GeoServerImportStatus.Failed,
                ResourcesProcessed = result.ApplyPlan?.Summary.TotalStepCount ??
                    result.WorkspacesImported + result.DataStoresImported + result.LayersImported + result.StylesImported,
                FailedResources = result.FailedResources,
                SourceGeoServerVersion = result.SourceGeoServerVersion ?? currentProgress.SourceGeoServerVersion,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = result.ErrorMessage,
                Warnings = result.Warnings,
                ApplyPlan = result.ApplyPlan ?? currentProgress.ApplyPlan,
                ApplyExecution = result.ApplyExecution ?? currentProgress.ApplyExecution,
                CurrentPhase = result.Success
                    ? (result.WasDryRun ? "Dry run completed" : result.ApplyExecution != null ? "Apply plan executed" : result.ApplyPlan != null ? "Apply plan generated" : "Import completed successfully")
                    : "Import failed"
            };

            await progressController.SetFinalProgressAsync(finalProgress, stoppingToken).ConfigureAwait(false);
            await _jobManager.RequestStore.DeleteProgressAsync(jobId, stoppingToken).ConfigureAwait(false);

            if (result.Success)
            {
                Log.JobCompleted(_logger, jobId, finalProgress.ResourcesProcessed, stopwatch.Elapsed.TotalSeconds);
            }
            else
            {
                Log.JobFailed(_logger, jobId, result.ErrorMessage ?? "Unknown error", stopwatch.Elapsed.TotalSeconds);
            }
        }
        catch (OperationCanceledException) when (jobCancellation.IsCancellationRequested)
        {
            stopwatch.Stop();

            if (Volatile.Read(ref leadershipLost) != 0)
            {
                acknowledgeCompletion = false;
                var resumableBaseProgress = progressController.CurrentProgress;
                var resumableProgress = (resumableBaseProgress ?? GeoServerImportProgress.CreateInitial(
                    jobId,
                    request?.GeoServerRestUrl ?? string.Empty,
                    request?.TargetHonuaUrl ?? string.Empty,
                    resumableBaseProgress?.EstimatedTotalResources,
                    resumableBaseProgress?.SourceGeoServerVersion)) with
                {
                    Status = GeoServerImportStatus.Queued,
                    CompletedAt = null,
                    ErrorMessage = null,
                    CurrentPhase = "Queued for recovery after leadership loss"
                };

                await progressController.SetProgressAsync(resumableProgress, CancellationToken.None).ConfigureAwait(false);
                Log.JobRequeuedAfterLeadershipLoss(_logger, jobId, stopwatch.Elapsed.TotalSeconds);
                return;
            }

            var currentProgress = progressController.CurrentProgress ?? GeoServerImportProgress.CreateInitial(
                jobId,
                request?.GeoServerRestUrl ?? string.Empty,
                request?.TargetHonuaUrl ?? string.Empty);
            var cancelledProgress = currentProgress with
            {
                Status = GeoServerImportStatus.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Import cancelled"
            };

            await progressController.SetFinalProgressAsync(cancelledProgress, CancellationToken.None).ConfigureAwait(false);
            await _jobManager.RequestStore.DeleteProgressAsync(jobId, CancellationToken.None).ConfigureAwait(false);

            Log.JobCancelled(_logger, jobId, stopwatch.Elapsed.TotalSeconds);
        }
        catch (Exception) when (jobCancellation.IsCancellationRequested)
        {
            stopwatch.Stop();

            if (Volatile.Read(ref leadershipLost) != 0)
            {
                acknowledgeCompletion = false;
                var resumableBaseProgress = progressController.CurrentProgress;
                var resumableProgress = (resumableBaseProgress ?? GeoServerImportProgress.CreateInitial(
                    jobId,
                    request?.GeoServerRestUrl ?? string.Empty,
                    request?.TargetHonuaUrl ?? string.Empty,
                    resumableBaseProgress?.EstimatedTotalResources,
                    resumableBaseProgress?.SourceGeoServerVersion)) with
                {
                    Status = GeoServerImportStatus.Queued,
                    CompletedAt = null,
                    ErrorMessage = null,
                    CurrentPhase = "Queued for recovery after leadership loss"
                };

                await progressController.SetProgressAsync(resumableProgress, CancellationToken.None).ConfigureAwait(false);
                Log.JobRequeuedAfterLeadershipLoss(_logger, jobId, stopwatch.Elapsed.TotalSeconds);
                return;
            }

            var currentProgress = progressController.CurrentProgress ?? GeoServerImportProgress.CreateInitial(
                jobId,
                request?.GeoServerRestUrl ?? string.Empty,
                request?.TargetHonuaUrl ?? string.Empty);
            var cancelledProgress = currentProgress with
            {
                Status = GeoServerImportStatus.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Import cancelled"
            };

            await progressController.SetFinalProgressAsync(cancelledProgress, CancellationToken.None).ConfigureAwait(false);
            await _jobManager.RequestStore.DeleteProgressAsync(jobId, CancellationToken.None).ConfigureAwait(false);

            Log.JobCancelled(_logger, jobId, stopwatch.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            var currentProgress = progressController.CurrentProgress ?? GeoServerImportProgress.CreateInitial(
                jobId,
                request?.GeoServerRestUrl ?? string.Empty,
                request?.TargetHonuaUrl ?? string.Empty);
            var failedProgress = currentProgress with
            {
                Status = GeoServerImportStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Import failed.",
                CurrentPhase = "Import failed with exception"
            };

            await progressController.SetFinalProgressAsync(failedProgress, CancellationToken.None).ConfigureAwait(false);
            await _jobManager.RequestStore.DeleteProgressAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            Log.JobException(_logger, jobId, ex);
        }
        finally
        {
            await ImportBackgroundServiceCoordinator.AcknowledgeCompletionAsync(
                _jobManager.JobQueue,
                jobId,
                acknowledgeCompletion,
                _logger,
                Log.JobCompletionAcknowledgeFailed).ConfigureAwait(false);

            await ImportBackgroundServiceCoordinator.StopMonitorAsync(monitorCancellation, monitorTask).ConfigureAwait(false);
        }
    }

    private static async Task<GeoServerImportRequest> ResolveSecretReferencesAsync(
        GeoServerImportRequest request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        if (string.IsNullOrWhiteSpace(request.PasswordSecretReference) &&
            string.IsNullOrWhiteSpace(request.HonuaApiKeySecretReference))
        {
            return request;
        }

        var resolvedPassword = request.Password;
        if (!string.IsNullOrWhiteSpace(request.PasswordSecretReference))
        {
            resolvedPassword = await ResolveRequiredSecretAsync(
                serviceProvider,
                request.PasswordSecretReference,
                "GeoServer password",
                cancellationToken).ConfigureAwait(false);
        }

        var resolvedHonuaApiKey = request.HonuaApiKey;
        if (!string.IsNullOrWhiteSpace(request.HonuaApiKeySecretReference))
        {
            resolvedHonuaApiKey = await ResolveRequiredSecretAsync(
                serviceProvider,
                request.HonuaApiKeySecretReference,
                "Honua API key",
                cancellationToken).ConfigureAwait(false);
        }

        return request with
        {
            Password = resolvedPassword,
            HonuaApiKey = resolvedHonuaApiKey
        };
    }

    private static async Task<string> ResolveRequiredSecretAsync(
        IServiceProvider serviceProvider,
        string secretReference,
        string secretDescription,
        CancellationToken cancellationToken)
    {
        if (SecretReferenceResolver.IsEnvironmentReference(secretReference))
        {
            var resolvedEnvironmentValue = SecretReferenceResolver.ResolveEnvironmentReference(secretReference, secretDescription);
            if (string.IsNullOrWhiteSpace(resolvedEnvironmentValue))
            {
                throw new SecretNotFoundException(secretReference, $"{secretDescription} secret reference could not be resolved.");
            }

            return resolvedEnvironmentValue;
        }

        var secretProvider = serviceProvider.GetService<ISecretProvider>()
            ?? throw new InvalidOperationException("GeoServer import secret references require secret provider services.");

        var resolved = await secretProvider.GetSecretAsync(secretReference, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new SecretNotFoundException(secretReference, $"{secretDescription} secret reference could not be resolved.");
        }

        return resolved;
    }

    private static bool IsTerminalStatus(GeoServerImportStatus status)
        => status is GeoServerImportStatus.Completed or GeoServerImportStatus.Failed or GeoServerImportStatus.Cancelled;

    private static partial class Log
    {
        [LoggerMessage(7960, LogLevel.Information, "GeoServer import background service starting (instance: {InstanceId})")]
        public static partial void ServiceStarting(ILogger logger, string instanceId);

        [LoggerMessage(7961, LogLevel.Information, "GeoServer import background service stopped (instance: {InstanceId})")]
        public static partial void ServiceStopped(ILogger logger, string instanceId);

        [LoggerMessage(7962, LogLevel.Debug, "Instance {InstanceId} is not the GeoServer import leader, waiting...")]
        public static partial void NotLeader(ILogger logger, string instanceId);

        [LoggerMessage(7963, LogLevel.Information, "Processing GeoServer import job {JobId}")]
        public static partial void JobStarted(ILogger logger, string jobId);

        [LoggerMessage(7964, LogLevel.Information, "GeoServer import job {JobId} completed: {ResourceCount} resources in {DurationSeconds:F1}s")]
        public static partial void JobCompleted(ILogger logger, string jobId, int resourceCount, double durationSeconds);

        [LoggerMessage(7965, LogLevel.Warning, "GeoServer import job {JobId} failed: {ErrorMessage} (duration: {DurationSeconds:F1}s)")]
        public static partial void JobFailed(ILogger logger, string jobId, string errorMessage, double durationSeconds);

        [LoggerMessage(7966, LogLevel.Warning, "GeoServer import job {JobId} cancelled (duration: {DurationSeconds:F1}s)")]
        public static partial void JobCancelled(ILogger logger, string jobId, double durationSeconds);

        [LoggerMessage(7967, LogLevel.Error, "GeoServer import job {JobId} threw exception")]
        public static partial void JobException(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(7968, LogLevel.Warning, "GeoServer import job {JobId} request not found in store")]
        public static partial void JobRequestNotFound(ILogger logger, string jobId);

        [LoggerMessage(7969, LogLevel.Warning, "Failed to update progress for GeoServer import job {JobId}")]
        public static partial void ProgressUpdateFailed(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(7970, LogLevel.Error, "Error in GeoServer import background processing loop")]
        public static partial void ProcessingError(ILogger logger, Exception exception);

        [LoggerMessage(7971, LogLevel.Warning, "Failed to poll cancellation state for GeoServer import job {JobId}")]
        public static partial void CancellationMonitorPollFailed(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(7972, LogLevel.Warning, "Failed to acknowledge completion for GeoServer import job {JobId}")]
        public static partial void JobCompletionAcknowledgeFailed(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(7973, LogLevel.Warning, "Leadership was lost while processing GeoServer import job {JobId}; cancelling local execution.")]
        public static partial void LeadershipLostDuringJob(ILogger logger, string jobId);

        [LoggerMessage(7974, LogLevel.Warning, "GeoServer import job {JobId} rejected due to unsafe GeoServer URL {GeoServerUrl}")]
        public static partial void JobRejectedUnsafeServiceUrl(ILogger logger, string jobId, string geoServerUrl);

        [LoggerMessage(7975, LogLevel.Warning, "GeoServer import job {JobId} yielded after leadership loss and will be recovered by the next durable leader (duration: {DurationSeconds:F1}s)")]
        public static partial void JobRequeuedAfterLeadershipLoss(ILogger logger, string jobId, double durationSeconds);
    }
}
