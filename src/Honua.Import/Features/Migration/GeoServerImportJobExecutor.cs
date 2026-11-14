// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Migration;

internal sealed partial class GeoServerImportJobExecutor : IJobExecutor
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GeoServerImportJobManager _jobManager;
    private readonly ILogger<GeoServerImportJobExecutor> _logger;
    private readonly IReadOnlyCollection<string>? _allowedHostSuffixes;
    private readonly ILicenseOperationPolicy? _licensePolicy;

    public GeoServerImportJobExecutor(
        IConfiguration configuration,
        IHostEnvironment hostEnvironment,
        IServiceScopeFactory scopeFactory,
        GeoServerImportJobManager jobManager,
        ILogger<GeoServerImportJobExecutor> logger,
        IOptions<MigrationUrlValidationOptions>? urlValidationOptions = null,
        ILicenseOperationPolicy? licensePolicy = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _hostEnvironment = hostEnvironment ?? throw new ArgumentNullException(nameof(hostEnvironment));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _jobManager = jobManager ?? throw new ArgumentNullException(nameof(jobManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _allowedHostSuffixes = urlValidationOptions?.Value.ResolveAllowedServiceHostSuffixes();
        _licensePolicy = licensePolicy;
    }

    public ExecutionJobKind Kind => ExecutionJobKind.GeoServerImport;

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job, IJobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);
        var jobId = job.OperationId;
        var request = _jobManager.ReadRequest(job);
        var stopwatch = Stopwatch.StartNew();
        Log.JobStarted(_logger, jobId);
        var licenseCancellation = _licensePolicy?.OperationCancellation ?? CancellationToken.None;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, licenseCancellation);
        using var progress = new ImportJobProgressController<GeoServerImportProgress>(jobId,
            _jobManager.ProgressStore, finalProgressTtl: ImportExecutionJobQueue<GeoServerImportRequest, GeoServerImportProgress>.Retention);
        var run = new ImportRun(jobId, request, progress, stopwatch, licenseCancellation, linked.Token);

        return await ExecuteRunAsync(run).ConfigureAwait(false);
    }

    private async Task<JobExecutionResult> ExecuteRunAsync(ImportRun run)
    {
        try
        {
            return await RunAsync(run).ConfigureAwait(false);
        }
        catch (Exception ex) when (run.LicenseCancellation.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            await run.Progress.SetFinalProgressAsync(run.Terminal(GeoServerImportStatus.Failed, "license expired",
                "Failed: license expired; partial import is incomplete"), CancellationToken.None).ConfigureAwait(false);
            return Failed("license expired");
        }
        catch (Exception ex) when (run.Token.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            await RecordOperatorCancellationAsync(run).ConfigureAwait(false);
            throw new OperationCanceledException("The import execution was interrupted.", ex, run.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            await run.Progress.SetFinalProgressAsync(run.Terminal(GeoServerImportStatus.Failed, "Import failed.",
                "Import failed with exception"), CancellationToken.None).ConfigureAwait(false);
            Log.JobException(_logger, run.JobId, ex);
            return Failed("Import failed.");
        }
    }

    private async Task RecordOperatorCancellationAsync(ImportRun run)
    {
        // Shutdown or lease loss leaves recovery to the worker; only durable operator cancellation
        // makes the import progress terminal here.
        var stored = await _jobManager.ProgressStore.GetProgressAsync(run.JobId, CancellationToken.None).ConfigureAwait(false);
        if (stored?.Status == GeoServerImportStatus.Cancelled)
        {
            await run.Progress.SetFinalProgressAsync(run.Terminal(GeoServerImportStatus.Cancelled, null,
                "Import cancelled"), CancellationToken.None).ConfigureAwait(false);
            Log.JobCancelled(_logger, run.JobId, run.Stopwatch.Elapsed.TotalSeconds);
        }
    }

    private async Task<JobExecutionResult> RunAsync(ImportRun run)
    {
        var (jobId, request, progress, stopwatch, _, _) = run;
        progress.Seed(await _jobManager.ProgressStore.GetProgressAsync(jobId, run.Token).ConfigureAwait(false));
        if (progress.CurrentProgress is { } terminal && GeoServerImportJobManager.IsTerminalStatus(terminal.Status))
        {
            return ReplayTerminal(terminal);
        }

        if (request is null)
        {
            Log.JobRequestNotFound(_logger, jobId);
            await progress.SetFinalProgressAsync(run.Terminal(GeoServerImportStatus.Failed, "Import request not found.",
                "Import request missing"), CancellationToken.None).ConfigureAwait(false);
            return Failed("Import request not found.");
        }

        request = request with { JobId = jobId };
        var validation = await GeoServerServiceUrlValidation.ValidateAsync(request.GeoServerRestUrl,
            GeoServerImportExecutionSettings.ShouldAllowUnsafeLocalUrls(_hostEnvironment, _configuration),
            _allowedHostSuffixes, run.Token).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            await progress.SetFinalProgressAsync(run.Terminal(GeoServerImportStatus.Failed, validation.ErrorMessage,
                "Import blocked by GeoServer URL validation"), CancellationToken.None).ConfigureAwait(false);
            Log.JobRejectedUnsafeServiceUrl(_logger, jobId, request.GeoServerRestUrl);
            return Failed(validation.ErrorMessage ?? "Import failed.");
        }

        var result = await ImportAsync(run, request).ConfigureAwait(false);
        return await CompleteAsync(run, result).ConfigureAwait(false);
    }

    private async Task<GeoServerImportResult> ImportAsync(ImportRun run, GeoServerImportRequest request)
    {
        var initial = run.Progress.CurrentProgress ?? GeoServerImportProgress.CreateInitial(run.JobId, request.GeoServerRestUrl, request.TargetHonuaUrl);
        await run.Progress.SetProgressAsync(initial with
        {
            Status = GeoServerImportStatus.Discovering,
            CompletedAt = null,
            ErrorMessage = null,
            CurrentPhase = "Starting import"
        }, run.Token).ConfigureAwait(false);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var resolvedRequest = await ResolveSecretReferencesAsync(request, scope.ServiceProvider, run.Token).ConfigureAwait(false);
        var importer = scope.ServiceProvider.GetRequiredService<IGeoServerImportService>();
        var reporter = new Progress<GeoServerImportProgress>(p =>
        {
            if (p.Status != GeoServerImportStatus.Queued)
            {
                _ = run.Progress.TryReportProgressAsync(p, _logger, Log.ProgressUpdateFailed);
            }
        });
        return await importer.ImportConfigurationAsync(resolvedRequest, reporter, run.Token).ConfigureAwait(false);
    }

    private async Task<JobExecutionResult> CompleteAsync(ImportRun run, GeoServerImportResult result)
    {
        var (jobId, request, progress, stopwatch, _, _) = run;
        run.LicenseCancellation.ThrowIfCancellationRequested();
        var stored = await _jobManager.ProgressStore.GetProgressAsync(jobId, run.Token).ConfigureAwait(false);
        if (stored?.Status == GeoServerImportStatus.Cancelled)
        {
            throw new OperationCanceledException("The import was cancelled while it ran.");
        }

        var final = BuildFinalProgress(progress.CurrentProgress!, result);
        await progress.SetFinalProgressAsync(final, run.Token).ConfigureAwait(false);
        if (result.Success)
        {
            Log.JobCompleted(_logger, jobId, final.ResourcesProcessed, stopwatch.Elapsed.TotalSeconds);
            return JobExecutionResult.Succeeded(result.Warnings);
        }

        Log.JobFailed(_logger, jobId, result.ErrorMessage ?? "Import failed.", stopwatch.Elapsed.TotalSeconds);
        return Failed(result.ErrorMessage ?? "Import failed.", result.Warnings);
    }

    private static GeoServerImportProgress BuildFinalProgress(
        GeoServerImportProgress current, GeoServerImportResult result)
        => current with
        {
            Status = result.Success ? GeoServerImportStatus.Completed : GeoServerImportStatus.Failed,
            ResourcesProcessed = result.ApplyPlan?.Summary.TotalStepCount ??
                result.WorkspacesImported + result.DataStoresImported + result.LayersImported + result.StylesImported,
            FailedResources = result.FailedResources,
            SourceGeoServerVersion = result.SourceGeoServerVersion ?? current.SourceGeoServerVersion,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = result.ErrorMessage,
            Warnings = result.Warnings,
            ApplyPlan = result.ApplyPlan ?? current.ApplyPlan,
            ApplyExecution = result.ApplyExecution ?? current.ApplyExecution,
            CurrentPhase = result.Success
                ? (result.WasDryRun ? "Dry run completed" : result.ApplyExecution != null ? "Apply plan executed" : result.ApplyPlan != null ? "Apply plan generated" : "Import completed successfully")
                : "Import failed"
        };

    private sealed record ImportRun(
        string JobId,
        GeoServerImportRequest? Request,
        ImportJobProgressController<GeoServerImportProgress> Progress,
        Stopwatch Stopwatch,
        CancellationToken LicenseCancellation,
        CancellationToken Token)
    {
        public GeoServerImportProgress Terminal(GeoServerImportStatus status, string? error, string phase)
            => (Progress.CurrentProgress ?? GeoServerImportProgress.CreateInitial(JobId,
                Request?.GeoServerRestUrl ?? string.Empty, Request?.TargetHonuaUrl ?? string.Empty)) with
            {
                Status = status,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = error,
                CurrentPhase = phase
            };
    }

    private static JobExecutionResult ReplayTerminal(GeoServerImportProgress progress) => progress.Status switch
    {
        GeoServerImportStatus.Cancelled => throw new OperationCanceledException("The import was cancelled before it started."),
        GeoServerImportStatus.Failed => Failed(progress.ErrorMessage ?? "Import failed."),
        _ => JobExecutionResult.Succeeded(progress.Warnings)
    };

    private static JobExecutionResult Failed(string message, IReadOnlyList<string>? warnings = null)
        => JobExecutionResult.Failed(message, warnings) with { IsRetryable = false };

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
            resolvedPassword = await RequestSecretReferenceValidation.ResolveRequiredAsync(
                serviceProvider,
                request.PasswordSecretReference,
                "GeoServer password",
                cancellationToken).ConfigureAwait(false);
        }

        var resolvedHonuaApiKey = request.HonuaApiKey;
        if (!string.IsNullOrWhiteSpace(request.HonuaApiKeySecretReference))
        {
            resolvedHonuaApiKey = await RequestSecretReferenceValidation.ResolveRequiredAsync(
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

    private static partial class Log
    {
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

        [LoggerMessage(7974, LogLevel.Warning, "GeoServer import job {JobId} rejected due to unsafe GeoServer URL {GeoServerUrl}")]
        public static partial void JobRejectedUnsafeServiceUrl(ILogger logger, string jobId, string geoServerUrl);

    }
}
