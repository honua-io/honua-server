// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Threading.Channels;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Progress;
using Honua.Server.Features.PrintingTools.Models;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.PrintingTools;

/// <summary>
/// Background service that processes queued print jobs for large or high-resolution map compositions.
/// </summary>
internal sealed class PrintingToolsBackgroundService : BackgroundService
{
    private readonly Channel<PrintJob> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PrintJobCancellationTokens _cancellationTokens;
    private readonly ILogger<PrintingToolsBackgroundService> _logger;

    public PrintingToolsBackgroundService(
        Channel<PrintJob> channel,
        IServiceScopeFactory scopeFactory,
        PrintJobCancellationTokens cancellationTokens,
        ILogger<PrintingToolsBackgroundService> logger)
    {
        _channel = channel;
        _scopeFactory = scopeFactory;
        _cancellationTokens = cancellationTokens;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                PrintingToolsLog.JobFailed(_logger, job.JobId, ex.Message, ex);
            }
        }
    }

    private async Task ProcessJobAsync(PrintJob job, CancellationToken stoppingToken)
    {
        using var linkedCts = _cancellationTokens.CreateLinkedTokenSource(job.JobId, stoppingToken);

        try
        {
            await ProcessJobCoreAsync(job, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            PrintingToolsLog.JobCancelled(_logger, job.JobId);
            await PersistCancelledStateAsync(job.JobId).ConfigureAwait(false);
        }
        finally
        {
            _cancellationTokens.Remove(job.JobId);
        }
    }

    private async Task ProcessJobCoreAsync(PrintJob job, CancellationToken cancellationToken)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("print.job", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, "PrintingTools");
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "job");
        activity?.SetTag("print.job_id", job.JobId);
        activity?.SetTag("print.template", job.TemplateName);
        activity?.SetTag("print.format", job.Format);
        activity?.SetTag("print.dpi", job.Dpi);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var progressStore = scope.ServiceProvider.GetRequiredService<IUniversalProgressStore>();
        var resourceValidator = scope.ServiceProvider.GetRequiredService<IResourceValidator>();
        var featureReader = scope.ServiceProvider.GetRequiredService<IFeatureReader>();
        var styleCatalog = scope.ServiceProvider.GetRequiredService<ILayerStyleCatalog>();
        var accessPolicyEvaluator = scope.ServiceProvider.GetRequiredService<IAccessPolicyEvaluator>();
        var temporaryFileService = scope.ServiceProvider.GetRequiredService<ITemporaryFileService>();

        // Check if the job was cancelled before we start processing
        var existing = await progressStore.GetProgressAsync(job.JobId, cancellationToken);
        if (existing is PrintProgress { Status: OperationStatus.Cancelled })
        {
            return;
        }

        // Advance to processing state
        var progress = existing is PrintProgress queued
            ? queued with { Status = OperationStatus.Processing, CurrentPhase = "Rendering map" }
            : PrintProgress.CreateInitial(job.JobId, job.Format, job.TemplateName, job.TotalElements) with
            {
                Status = OperationStatus.Processing,
                CurrentPhase = "Rendering map"
            };
        await progressStore.SetProgressAsync(job.JobId, progress, PrintingToolsRequestHandlers.ResultTtl, cancellationToken);

        try
        {
            var result = await PrintingToolsRequestHandlers.ExecuteAsync(
                job.WebMap,
                job.Format,
                job.TemplateName,
                job.Dpi,
                resourceValidator,
                featureReader,
                styleCatalog,
                _logger,
                cancellationToken,
                callerPrincipal: job.CallerPrincipal,
                accessPolicyEvaluator: accessPolicyEvaluator);

            if (result is null)
            {
                var failed = progress with
                {
                    Status = OperationStatus.Failed,
                    ErrorMessage = "Print composition failed. Verify template, format, and edition compatibility.",
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Print failed"
                };
                await progressStore.SetProgressAsync(job.JobId, failed, PrintingToolsRequestHandlers.ResultTtl, CancellationToken.None);
                PrintingToolsLog.JobFailed(_logger, job.JobId, "Print composition failed.");
                return;
            }

            var (outputBytes, contentType, fileName) = result.Value;

            // Check cancellation before storing the file to avoid orphaning
            // a temp file when a cancel request arrived during rendering.
            // A narrow race remains between this check and the store call, but
            // the post-store check below covers that; the TTL-based cleanup is
            // the final safety net.
            var preStoreProgress = await progressStore.GetProgressAsync(job.JobId, CancellationToken.None);
            if (preStoreProgress is PrintProgress { Status: OperationStatus.Cancelled })
            {
                return;
            }

            // Once rendering is complete, the completion path must not be interrupted
            // by an incoming cancellation signal.  Using CancellationToken.None here
            // ensures that a cancel request racing with job completion cannot disrupt
            // the temp-file store or the Completed state write, which would leave
            // a finished job without a downloadable result.
            //
            // When the admin cancel endpoint's Cancel() call reaches the CTS
            // (workerOwnsTerminalState = true), terminal-state ownership is
            // delegated to this worker.  However, when Cancel() returns false
            // (job not yet dequeued, or CTS registration gap), the admin
            // endpoint's fallback path may write Cancelled directly.  The
            // post-write guard below re-asserts Completed if that race occurs.
            string downloadUrl;
            try
            {
                downloadUrl = await temporaryFileService.StoreTemporaryFileAsync(
                    outputBytes,
                    contentType,
                    PrintingToolsRequestHandlers.ResultTtl,
                    principal: job.CallerPrincipal,
                    cancellationToken: CancellationToken.None);
            }
            catch (TemporaryStorageLimitExceededException)
            {
                var storageFailed = progress with
                {
                    Status = OperationStatus.Failed,
                    ErrorMessage = "Temporary printing storage is currently at capacity. Please retry shortly.",
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Print failed"
                };
                await progressStore.SetProgressAsync(job.JobId, storageFailed, PrintingToolsRequestHandlers.ResultTtl, CancellationToken.None);
                PrintingToolsLog.JobFailed(_logger, job.JobId, "Temporary storage at capacity.");
                return;
            }

            // Warnings were already collected and persisted at submit time (SubmitJobInternalAsync).
            // The `progress` record carries them through via the `queued with {...}` expression,
            // so we propagate rather than re-collecting from the same WebMap input.
            var completed = progress with
            {
                Status = OperationStatus.Completed,
                ProcessedElements = job.TotalElements,
                OutputSizeBytes = outputBytes.LongLength,
                DownloadUrl = downloadUrl,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Print completed"
            };
            await progressStore.SetProgressAsync(job.JobId, completed, PrintingToolsRequestHandlers.ResultTtl, CancellationToken.None);

            // Guard: a racing admin-cancel fallback can overwrite Completed
            // with Cancelled when the cancel request arrives in the narrow
            // window between channel dequeue and CTS registration (before
            // Cancel() can return true).  Since the output file is already
            // stored, Completed is the authoritative terminal state.
            var postWrite = await progressStore.GetProgressAsync(job.JobId, CancellationToken.None);
            if (postWrite is PrintProgress { Status: not OperationStatus.Completed })
            {
                await progressStore.SetProgressAsync(job.JobId, completed, PrintingToolsRequestHandlers.ResultTtl, CancellationToken.None);
            }

            // Second guard: yield to let any in-flight admin-cancel write settle,
            // then re-assert Completed if it was overwritten.  Together with the
            // pre-write check added to the admin cancel endpoint, this makes the
            // race window vanishingly small (formal closure requires CAS semantics
            // in IUniversalProgressStore, tracked as follow-on).
            await Task.Yield();
            var finalGuard = await progressStore.GetProgressAsync(job.JobId, CancellationToken.None);
            if (finalGuard is PrintProgress { Status: not OperationStatus.Completed })
            {
                await progressStore.SetProgressAsync(job.JobId, completed, PrintingToolsRequestHandlers.ResultTtl, CancellationToken.None);
            }

            activity?.SetTag("print.output_bytes", outputBytes.LongLength);
            activity?.SetStatus(ActivityStatusCode.Ok);
            PrintingToolsLog.JobCompleted(_logger, job.JobId, outputBytes.LongLength);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            PrintingToolsLog.JobFailed(_logger, job.JobId, ex.Message, ex);

            try
            {
                var failed = progress with
                {
                    Status = OperationStatus.Failed,
                    ErrorMessage = "An internal error occurred during print processing.",
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Print failed"
                };
                await progressStore.SetProgressAsync(job.JobId, failed, PrintingToolsRequestHandlers.ResultTtl, CancellationToken.None);
            }
            catch (Exception progressEx)
            {
                PrintingToolsLog.JobProgressPersistFailed(_logger, job.JobId, ex.Message, progressEx);
            }
        }
    }

    private async Task PersistCancelledStateAsync(string jobId)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var progressStore = scope.ServiceProvider.GetRequiredService<IUniversalProgressStore>();
            var existing = await progressStore.GetProgressAsync(jobId, CancellationToken.None);
            if (existing is PrintProgress { Status: not OperationStatus.Cancelled } p)
            {
                var cancelled = p with
                {
                    Status = OperationStatus.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Cancelled"
                };
                await progressStore.SetProgressAsync(jobId, cancelled, PrintingToolsRequestHandlers.ResultTtl, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            PrintingToolsLog.JobCancelPersistFailed(_logger, jobId, ex);
        }
    }
}
