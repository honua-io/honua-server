// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Import.FileImport.Durable;

/// <summary>
/// Worker-side <see cref="IJobExecutor"/> for <see cref="ExecutionJobKind.FileImport"/> jobs
/// (honua-server#5916). Runs on the shared <c>JobExecutionService</c> loop: on a long-lived
/// server for the local backend, or in the batch-launched execution worker (AWS Batch on the
/// serverless cell). It reads the staged object from file storage through the existing
/// <see cref="IFileImportService"/> pipeline and keeps the import job document
/// (<c>GET /api/v1/admin/import/jobs/{id}</c>, keyed by the operation id) current.
/// </summary>
internal sealed partial class FileImportJobExecutor(
    IServiceScopeFactory scopeFactory,
    IUniversalProgressStore progressStore,
    ILogger<FileImportJobExecutor> logger,
    IInstanceIdentity? instanceIdentity = null,
    Honua.Core.Features.Licensing.Abstractions.ILicenseOperationPolicy? licensePolicy = null) : IJobExecutor
{
    /// <summary>Set by AWS Batch in every job container; identifies the executing Batch job.</summary>
    internal const string AwsBatchJobIdVariable = "AWS_BATCH_JOB_ID";

    internal const string SafeImportFailureMessage = "Import failed.";
    internal const string LicenseExpiredMessage = "license expired";
    private static readonly TimeSpan ProgressRetention = TimeSpan.FromDays(1);

    public ExecutionJobKind Kind => ExecutionJobKind.FileImport;

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var jobId = job.OperationId;
        if (!FileImportJobParameters.TryParse(job.Spec.Parameters, out var parsed, out var fileSize, out var parseError))
        {
            Log.InvalidSpec(logger, jobId, parseError ?? "unknown");
            return JobExecutionResult.Failed($"Invalid file-import job spec: {parseError}");
        }

        var request = parsed!;
        var runner = job.Spec.Parameters.TryGetValue(FileImportJobParameters.Runner, out var declaredRunner)
            && !string.IsNullOrWhiteSpace(declaredRunner)
                ? declaredRunner
                : ImportJobRunners.InProcess;
        var instanceId = ResolveInstanceId(runner);

        var existing = await progressStore.GetProgressAsync<ImportProgress>(jobId, cancellationToken).ConfigureAwait(false);
        var processing = (existing ?? CreateInitialProgress(jobId, request, fileSize)) with
        {
            Status = ImportStatus.Processing,
            CompletedAt = null,
            ErrorMessage = null,
            CurrentPhase = "Processing",
            Runner = runner,
            InstanceId = instanceId,
        };
        await progressStore.SetProgressAsync(jobId, processing, ProgressRetention, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(0, "Importing", cancellationToken).ConfigureAwait(false);
        Log.ImportStarted(logger, jobId, request.TableName, runner, instanceId ?? "unknown");

        var writer = new SerializedProgressWriter(progressStore, logger, jobId);
        var progress = new Progress<ImportProgress>(p => writer.Report(p with
        {
            JobId = jobId,
            OperationInstanceId = request.OperationInstanceId,
            CorrelationId = request.CorrelationId,
            AuditId = request.AuditId,
            ProposalId = request.ProposalId,
            SourceKind = processing.SourceKind,
            SourceUrl = processing.SourceUrl,
            CloudFileId = processing.CloudFileId,
            UploadId = processing.UploadId,
            Runner = runner,
            InstanceId = instanceId,
        }));

        // The worker loop already links license revocation into the job token; link it here too so
        // a revoked licence stops the import even under a custom token, and is reported as such.
        var licenseCancellation = licensePolicy?.OperationCancellation ?? CancellationToken.None;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, licenseCancellation);
        cancellationToken = linked.Token;

        ImportResult result;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var importService = scope.ServiceProvider.GetRequiredService<IFileImportService>();
            result = await importService.ImportFileAsync(request, progress, cancellationToken).ConfigureAwait(false);

            if (result.Success)
            {
                await ImportedSnapshotRefresher.RefreshAsync(
                    scope.ServiceProvider,
                    request.TargetSchema,
                    request.TableName,
                    ex => Log.SnapshotRefreshFailed(logger, jobId, ex),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Operator cancel, timeout or worker shutdown: JobExecutionService owns the durable
            // outcome (cancelled, failed or handed back for retry), and the job-status read
            // projects a terminal execution record onto this document.
            await writer.CloseAndDrainAsync().ConfigureAwait(false);
            if (licenseCancellation.IsCancellationRequested)
            {
                // Parity with the in-process runner: a partial import stopped by licence expiry is a
                // terminal failure, never a retry.
                await WriteTerminalAsync(jobId, processing, ImportStatus.Failed, null, LicenseExpiredMessage).ConfigureAwait(false);
                return NonRetryableFailure(LicenseExpiredMessage);
            }

            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Deliberately broad: any importer fault is a failed import. The detail is logged;
            // the job document carries the same safe message the in-process runner reports.
            Log.ImportFaulted(logger, jobId, ex);
            await writer.CloseAndDrainAsync().ConfigureAwait(false);
            await WriteTerminalAsync(jobId, processing, ImportStatus.Failed, null, SafeImportFailureMessage).ConfigureAwait(false);
            return NonRetryableFailure(SafeImportFailureMessage);
        }

        await writer.CloseAndDrainAsync().ConfigureAwait(false);
        if (!result.Success)
        {
            Log.ImportFailed(logger, jobId, result.ErrorMessage ?? "unknown");
            await WriteTerminalAsync(jobId, processing, ImportStatus.Failed, result, SafeImportFailureMessage).ConfigureAwait(false);
            return NonRetryableFailure(SafeImportFailureMessage, result.Warnings);
        }

        await WriteTerminalAsync(jobId, processing, ImportStatus.Completed, result, result.ErrorMessage).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Import completed", cancellationToken).ConfigureAwait(false);
        Log.ImportCompleted(logger, jobId, request.TableName, result.FeatureCount);
        return JobExecutionResult.Succeeded(result.Warnings);
    }

    /// <summary>
    /// An importer failure is deterministic for the staged input (format, schema, constraint):
    /// the job document is already terminal, so the durable runner must not retry it. Infrastructure
    /// loss (worker crash, Spot interruption) still goes through the retry policy.
    /// </summary>
    private static JobExecutionResult NonRetryableFailure(string message, IReadOnlyList<string>? warnings = null)
        => JobExecutionResult.Failed(message, warnings) with { IsRetryable = false };

    private string? ResolveInstanceId(string runner)
    {
        if (string.Equals(runner, ImportJobRunners.Batch, StringComparison.Ordinal)
            && Environment.GetEnvironmentVariable(AwsBatchJobIdVariable) is { Length: > 0 } batchJobId)
        {
            return batchJobId;
        }

        return instanceIdentity?.InstanceId;
    }

    private async Task WriteTerminalAsync(
        string jobId,
        ImportProgress fallback,
        ImportStatus status,
        ImportResult? result,
        string? errorMessage)
    {
        var current = await progressStore.GetProgressAsync<ImportProgress>(jobId, CancellationToken.None).ConfigureAwait(false)
            ?? fallback;
        var terminal = current with
        {
            Status = status,
            FeaturesProcessed = result?.FeatureCount ?? current.FeaturesProcessed,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = errorMessage,
            Warnings = result?.Warnings ?? current.Warnings,
            DetectedSrid = result?.DetectedSrid ?? current.DetectedSrid,
            CurrentPhase = status == ImportStatus.Completed ? "Import completed" : "Import failed",
            Runner = fallback.Runner,
            InstanceId = fallback.InstanceId,
        };
        await progressStore.SetProgressAsync(jobId, terminal, ProgressRetention, CancellationToken.None).ConfigureAwait(false);
    }

    private static ImportProgress CreateInitialProgress(string jobId, ImportRequest request, long fileSize)
        => ImportProgress.CreateInitial(
            jobId,
            request.TableName,
            default,
            fileSize > 0 ? fileSize : null,
            request.FileName,
            request.SourceKind,
            request.SourceUrl,
            request.CloudFileId,
            request.UploadId,
            operationInstanceId: request.OperationInstanceId,
            correlationId: request.CorrelationId,
            auditId: request.AuditId,
            proposalId: request.ProposalId);

    /// <summary>Writes progress ticks in order without blocking the importer.</summary>
    private sealed class SerializedProgressWriter(IUniversalProgressStore store, ILogger logger, string jobId)
    {
        private readonly Lock _gate = new();
        private Task _tail = Task.CompletedTask;
        private bool _closed;

        public void Report(ImportProgress progress)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                var previous = _tail;
                _tail = WriteAfterAsync(previous, progress);
            }
        }

        public Task CloseAndDrainAsync()
        {
            lock (_gate)
            {
                _closed = true;
                return _tail;
            }
        }

        private async Task WriteAfterAsync(Task previous, ImportProgress progress)
        {
            await previous.ConfigureAwait(false);
            try
            {
                await store.SetProgressAsync(jobId, progress, ProgressRetention, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Deliberately broad: a progress-store fault must not break the ordered chain or
                // fail the import; the terminal write records the outcome.
                Log.ProgressWriteFailed(logger, jobId, ex);
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(7480, LogLevel.Warning, "File-import job {JobId} has an invalid spec: {Error}")]
        public static partial void InvalidSpec(ILogger logger, string jobId, string error);

        [LoggerMessage(7481, LogLevel.Information, "File-import job {JobId} started table={TableName} runner={Runner} instance={InstanceId}")]
        public static partial void ImportStarted(ILogger logger, string jobId, string tableName, string runner, string instanceId);

        [LoggerMessage(7482, LogLevel.Information, "File-import job {JobId} completed table={TableName} features={FeatureCount}")]
        public static partial void ImportCompleted(ILogger logger, string jobId, string tableName, int featureCount);

        [LoggerMessage(7483, LogLevel.Warning, "File-import job {JobId} failed: {Error}")]
        public static partial void ImportFailed(ILogger logger, string jobId, string error);

        [LoggerMessage(7484, LogLevel.Error, "File-import job {JobId} threw")]
        public static partial void ImportFaulted(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(7485, LogLevel.Warning, "File-import job {JobId} could not refresh the published snapshot")]
        public static partial void SnapshotRefreshFailed(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(7486, LogLevel.Warning, "File-import job {JobId} could not write progress")]
        public static partial void ProgressWriteFailed(ILogger logger, string jobId, Exception exception);
    }
}
