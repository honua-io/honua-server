// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Migration;

internal sealed partial class GeoservicesImportJobExecutor : IJobExecutor, IJobCancellationRecoveryExecutor
{
    internal const string ImportFailedMessage = "Import failed.";
    internal const string LicenseExpiredMessage = "license expired";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GeoservicesImportJobManager _jobManager;
    private readonly ILicenseOperationPolicy? _licensePolicy;
    private readonly IReadOnlyCollection<string>? _allowedHostSuffixes;
    private readonly ILogger<GeoservicesImportJobExecutor> _logger;

    public GeoservicesImportJobExecutor(
        IServiceScopeFactory scopeFactory,
        GeoservicesImportJobManager jobManager,
        ILogger<GeoservicesImportJobExecutor> logger,
        IOptions<MigrationUrlValidationOptions>? urlValidationOptions = null,
        ILicenseOperationPolicy? licensePolicy = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _jobManager = jobManager ?? throw new ArgumentNullException(nameof(jobManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _allowedHostSuffixes = urlValidationOptions?.Value.ResolveAllowedServiceHostSuffixes();
        _licensePolicy = licensePolicy;
    }

    public ExecutionJobKind Kind => ExecutionJobKind.GeoservicesImport;

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var jobId = job.OperationId;
        Log.JobStarted(_logger, jobId);
        var request = _jobManager.ReadRequest(job);
        if (request is null)
        {
            Log.JobRequestNotFound(_logger, jobId);
            return await FailMissingRequestAsync(jobId).ConfigureAwait(false);
        }

        var licenseCancellation = _licensePolicy?.OperationCancellation ?? CancellationToken.None;
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, licenseCancellation);
        using var progress = new ImportJobProgressController<GeoservicesImportProgress>(
            jobId,
            _jobManager.ProgressStore,
            finalProgressTtl: ImportExecutionJobQueue<GeoservicesImportRequest, GeoservicesImportProgress>.Retention);
        var run = new ImportRun(jobId, request with { JobId = jobId }, progress, context, Stopwatch.StartNew(), licenseCancellation, jobCancellation.Token);

        var recovered = await RecoverAsync(run).ConfigureAwait(false);
        return recovered ?? await ExecuteRunAsync(run).ConfigureAwait(false);
    }

    private async Task<JobExecutionResult> ExecuteRunAsync(ImportRun run)
    {
        GeoservicesImportResult result;
        try
        {
            var rejection = await ValidateSourceAsync(run).ConfigureAwait(false);
            if (rejection is not null)
            {
                return rejection;
            }

            result = await RunAsync(run).ConfigureAwait(false);
        }
        catch (Exception ex) when (run.LicenseCancellation.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            await run.Progress.SetFinalProgressAsync(run.Terminal(GeoservicesImportStatus.Failed, LicenseExpiredMessage,
                "Failed: license expired; partial import is incomplete"), CancellationToken.None).ConfigureAwait(false);
            return NotRetryable(LicenseExpiredMessage);
        }
        catch (Exception ex) when (run.Token.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            await RecordOperatorCancellationAsync(run).ConfigureAwait(false);
            if (ex is OperationCanceledException)
            {
                throw;
            }

            throw new OperationCanceledException("The import was cancelled while it ran.", ex, run.Token);
        }
        // Intentionally generic: the import job's top-level boundary records any importer fault as a
        // failed job instead of handing it to the retry policy; the job document is already terminal.
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            await run.Progress.SetFinalProgressAsync(run.Terminal(GeoservicesImportStatus.Failed, ImportFailedMessage,
                "Import failed with exception"), CancellationToken.None).ConfigureAwait(false);
            Log.JobException(_logger, run.JobId, ex);
            return NotRetryable(ImportFailedMessage);
        }

        // A committed import remains recoverable when execution-store bookkeeping fails.
        return await CompleteAsync(run, result).ConfigureAwait(false);
    }

    private async Task<JobExecutionResult?> ValidateSourceAsync(ImportRun run)
    {
        var jobId = run.JobId;
        var validation = await GeoservicesServiceUrlValidation.ValidateAsync(
            run.Request.ServiceUrl, _allowedHostSuffixes, run.Token).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            await run.Progress.SetFinalProgressAsync(run.Terminal(GeoservicesImportStatus.Failed, validation.ErrorMessage,
                "Import blocked by service URL validation"), CancellationToken.None).ConfigureAwait(false);
            Log.JobRejectedUnsafeServiceUrl(_logger, jobId, run.Request.ServiceUrl);
            return NotRetryable(validation.ErrorMessage ?? ImportFailedMessage);
        }

        return null;
    }

    private async Task<GeoservicesImportResult> RunAsync(ImportRun run)
    {
        if (run.Progress.CurrentProgress is { } queued)
        {
            await run.Progress.SetProgressAsync(queued with
            {
                Status = GeoservicesImportStatus.Discovering,
                CompletedAt = null,
                ErrorMessage = null,
                CurrentPhase = "Discovering layer metadata"
            }, run.Token).ConfigureAwait(false);
        }

        var result = await ImportAsync(run).ConfigureAwait(false);
        run.Stopwatch.Stop();
        return result;
    }

    private async Task<GeoservicesImportResult> ImportAsync(ImportRun run)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        GeoservicesImportPlanning.RestoreTenant(scope.ServiceProvider, run.Request.Target);
        var request = await GeoservicesCredentialResolution.ResolveSecretReferencesAsync(
            run.Request, scope.ServiceProvider, run.Token).ConfigureAwait(false);
        var importService = scope.ServiceProvider.GetRequiredService<IGeoservicesImportService>();
        var reporter = new Progress<GeoservicesImportProgress>(p =>
        {
            // Only the returned result contains the complete committed/publication outcome.
            // Terminal callbacks must not win the durable progress terminal-write guard.
            if (p.Status != GeoservicesImportStatus.Queued &&
                !GeoservicesImportJobManager.IsTerminalStatus(p.Status))
            {
                _ = run.Progress.TryReportProgressAsync(p, _logger, Log.ProgressUpdateFailed);
            }
        });
        return await importService.ImportLayerAsync(request, reporter, run.Token).ConfigureAwait(false);
    }

    private async Task<JobExecutionResult> CompleteAsync(ImportRun run, GeoservicesImportResult result)
    {
        var jobId = run.JobId;
        if (run.LicenseCancellation.IsCancellationRequested)
        {
            return await FailExpiredLicenseAsync(run, result).ConfigureAwait(false);
        }

        if (result.DataCommitted)
        {
            try
            {
                await RecordCommitAsync(run).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (run.LicenseCancellation.IsCancellationRequested)
            {
                return await FailExpiredLicenseAsync(run, result).ConfigureAwait(false);
            }
        }

        // The cancel endpoint writes Cancelled first. That wins only when the import had not yet
        // committed; a committed result is kept instead of being discarded for the Cancelled stamp.
        var stored = await _jobManager.ProgressStore.GetProgressAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        if (stored?.Status == GeoservicesImportStatus.Cancelled && !result.DataCommitted)
        {
            Log.JobCancelled(_logger, jobId, run.Stopwatch.Elapsed.TotalSeconds);
            throw new OperationCanceledException("The import was cancelled before it committed.");
        }

        var finalProgress = BuildFinalProgress(run, result);
        // A cancel that arrived after commit has already cancelled the job token, so the terminal
        // write of committed data uses a token cancellation cannot abort.
        await run.Progress.SetFinalProgressAsync(finalProgress, result.DataCommitted ? CancellationToken.None : run.Token)
            .ConfigureAwait(false);
        LogOutcome(jobId, result, run.Stopwatch.Elapsed.TotalSeconds);
        return finalProgress.Status == GeoservicesImportStatus.Failed
            ? NotRetryable(result.ErrorMessage ?? ImportFailedMessage, result.Warnings)
            : JobExecutionResult.Succeeded(result.Warnings) with { CompletedWithCommittedEffects = result.DataCommitted };
    }

    private async Task<JobExecutionResult> FailExpiredLicenseAsync(ImportRun run, GeoservicesImportResult result)
    {
        var message = result.DataCommitted ? "license expired; imported rows were already committed" : LicenseExpiredMessage;
        await run.Progress.SetFinalProgressAsync(run.Terminal(GeoservicesImportStatus.Failed, message,
            result.DataCommitted
                ? "Failed: license expired after data was committed"
                : "Failed: license expired; partial import is incomplete") with
        {
            DataCommitted = result.DataCommitted,
            FeaturesProcessed = result.FeatureCount,
            FailedFeatures = result.FailedFeatures,
            PublishedLayerId = result.PublishedLayerId
        }, CancellationToken.None).ConfigureAwait(false);
        Log.JobFailed(_logger, run.JobId, message, run.Stopwatch.Elapsed.TotalSeconds);
        return NotRetryable(message);
    }

    private static GeoservicesImportProgress BuildFinalProgress(ImportRun run, GeoservicesImportResult result)
    {
        var request = run.Request;
        var finalStatus = result.Success
            ? GeoservicesImportStatus.Completed
            : result.NeedsReview ? GeoservicesImportStatus.NeedsReview : GeoservicesImportStatus.Failed;
        return new GeoservicesImportProgress
        {
            JobId = run.JobId,
            Status = finalStatus,
            DataCommitted = result.DataCommitted,
            FeaturesProcessed = result.FeatureCount,
            FailedFeatures = result.FailedFeatures,
            SourceServiceUrl = request.ServiceUrl,
            SourceLayerId = request.LayerId,
            SourceLayerName = result.SourceLayerName,
            TableName = request.TableName,
            ServiceName = result.ServiceName ?? request.ServiceName,
            PublishedLayerId = result.PublishedLayerId,
            StartedAt = run.StartedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = result.ErrorMessage,
            ErrorCode = result.ErrorCode,
            Warnings = result.Warnings,
            AttachmentsProcessed = result.AttachmentCount,
            FailedAttachments = result.FailedAttachments,
            ReconciliationArtifact = result.ReconciliationArtifact,
            CatalogReconciliationReport = result.CatalogReconciliationReport,
            FidelityVerdict = result.FidelityVerdict,
            FidelityDifferences = result.FidelityDifferences,
            CurrentPhase = FinalPhase(result, finalStatus)
        };
    }

    private static string FinalPhase(GeoservicesImportResult result, GeoservicesImportStatus finalStatus)
    {
        if (result.DataCommitted && !result.Success && result.ErrorMessage != null
            && result.ErrorMessage.StartsWith("Import was cancelled after the data was committed", StringComparison.Ordinal))
        {
            return "Import cancelled after data was committed";
        }

        return finalStatus switch
        {
            GeoservicesImportStatus.Completed => result.PublishedLayerId.HasValue
                ? "Import completed and layer published"
                : "Import completed",
            GeoservicesImportStatus.NeedsReview => "Import published but requires operator review (fidelity gate)",
            _ => "Import failed"
        };
    }

    private void LogOutcome(string jobId, GeoservicesImportResult result, double seconds)
    {
        if (result.Success)
        {
            Log.JobCompleted(_logger, jobId, result.FeatureCount, seconds);
        }
        else if (result.NeedsReview)
        {
            Log.JobNeedsReview(_logger, jobId, result.FidelityVerdict, seconds);
        }
        else
        {
            Log.JobFailed(_logger, jobId, result.ErrorMessage ?? "Unknown error", seconds);
        }
    }

    // Worker shutdown, lease loss and timeout leave the document to the next attempt or to the
    // ended-job projection; only an operator cancel, which wrote Cancelled first, is recorded here.
    private async Task RecordOperatorCancellationAsync(ImportRun run)
    {
        var stored = await _jobManager.ProgressStore.GetProgressAsync(run.JobId, CancellationToken.None).ConfigureAwait(false);
        if (stored?.Status != GeoservicesImportStatus.Cancelled)
        {
            return;
        }

        await run.Progress.SetFinalProgressAsync(run.Terminal(GeoservicesImportStatus.Cancelled, null, "Import cancelled"),
            CancellationToken.None).ConfigureAwait(false);
        Log.JobCancelled(_logger, run.JobId, run.Stopwatch.Elapsed.TotalSeconds);
    }

    private async Task<JobExecutionResult> FailMissingRequestAsync(string jobId)
    {
        var progress = await _jobManager.ProgressStore.GetProgressAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        if (progress != null && !GeoservicesImportJobManager.IsTerminalStatus(progress.Status))
        {
            await _jobManager.ProgressStore.SetProgressAsync(jobId, progress with
            {
                Status = GeoservicesImportStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Import request not found.",
                CurrentPhase = "Import request missing"
            }, ImportExecutionJobQueue<GeoservicesImportRequest, GeoservicesImportProgress>.Retention, CancellationToken.None)
                .ConfigureAwait(false);
        }

        return NotRetryable("Import request is missing or unreadable.");
    }

    // A redelivered job whose document is already terminal reports that outcome without importing again.
    private static JobExecutionResult ReplayTerminal(GeoservicesImportProgress progress) => progress.Status switch
    {
        GeoservicesImportStatus.Cancelled => throw new OperationCanceledException("The import was cancelled before it started."),
        GeoservicesImportStatus.Failed => NotRetryable(progress.ErrorMessage ?? ImportFailedMessage),
        _ => JobExecutionResult.Succeeded(progress.Warnings) with { CompletedWithCommittedEffects = progress.DataCommitted }
    };

    private static Task RecordCommitAsync(ImportRun run)
        => run.Context.RecordCommittedEffectAsync($"honua:import:{run.JobId}:committed", CancellationToken.None);

    private static JobExecutionResult NotRetryable(string message, IReadOnlyList<string>? warnings = null)
        => JobExecutionResult.Failed(message, warnings) with { IsRetryable = false };

    private sealed record ImportRun(
        string JobId,
        GeoservicesImportRequest Request,
        ImportJobProgressController<GeoservicesImportProgress> Progress,
        IJobExecutionContext Context,
        Stopwatch Stopwatch,
        CancellationToken LicenseCancellation,
        CancellationToken Token)
    {
        public DateTimeOffset StartedAt => Progress.CurrentProgress?.StartedAt ?? DateTimeOffset.UtcNow.Subtract(Stopwatch.Elapsed);

        public GeoservicesImportProgress Terminal(GeoservicesImportStatus status, string? errorMessage, string phase)
            => (Progress.CurrentProgress ?? GeoservicesImportProgress.CreateInitial(
                JobId, Request.ServiceUrl, Request.LayerId, Request.TableName)) with
            {
                Status = status,
                StartedAt = StartedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = errorMessage,
                CurrentPhase = phase
            };
    }

    private static partial class Log
    {
        [LoggerMessage(7703, LogLevel.Information, "Processing import job {JobId}")]
        public static partial void JobStarted(ILogger logger, string jobId);

        [LoggerMessage(7704, LogLevel.Information, "Import job {JobId} completed: {FeatureCount} features in {DurationSeconds:F1}s")]
        public static partial void JobCompleted(ILogger logger, string jobId, int featureCount, double durationSeconds);

        [LoggerMessage(7705, LogLevel.Warning, "Import job {JobId} failed: {ErrorMessage} (duration: {DurationSeconds:F1}s)")]
        public static partial void JobFailed(ILogger logger, string jobId, string errorMessage, double durationSeconds);

        [LoggerMessage(7706, LogLevel.Warning, "Import job {JobId} cancelled (duration: {DurationSeconds:F1}s)")]
        public static partial void JobCancelled(ILogger logger, string jobId, double durationSeconds);

        [LoggerMessage(7707, LogLevel.Error, "Import job {JobId} threw exception")]
        public static partial void JobException(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(7708, LogLevel.Warning, "Import job {JobId} has no readable request on its execution job")]
        public static partial void JobRequestNotFound(ILogger logger, string jobId);

        [LoggerMessage(7709, LogLevel.Warning, "Failed to update progress for job {JobId}")]
        public static partial void ProgressUpdateFailed(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(7712, LogLevel.Warning, "Import job {JobId} blocked by service URL validation: {ServiceUrl}")]
        public static partial void JobRejectedUnsafeServiceUrl(ILogger logger, string jobId, string serviceUrl);

        [LoggerMessage(7716, LogLevel.Warning, "Import job {JobId} requires operator review (fidelity verdict {FidelityVerdict}, duration: {DurationSeconds:F1}s)")]
        public static partial void JobNeedsReview(ILogger logger, string jobId, string fidelityVerdict, double durationSeconds);
    }
}
