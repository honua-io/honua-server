// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.ObjectStore;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Import.FileImport.Resumable;

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
    Honua.Core.Features.Licensing.Abstractions.ILicenseOperationPolicy? licensePolicy = null) : IJobExecutor, IJobCommittedResultRecovery
{
    /// <summary>Set by AWS Batch in every job container; identifies the executing Batch job.</summary>
    internal const string AwsBatchJobIdVariable = "AWS_BATCH_JOB_ID";

    internal const string SafeImportFailureMessage = "Import failed.";
    internal const string LicenseExpiredMessage = "license expired";
    private static readonly TimeSpan ProgressRetention = TimeSpan.FromDays(1);

    public ExecutionJobKind Kind => ExecutionJobKind.FileImport;

    private async Task<JobExecutionResult> ExecuteCoreAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken, bool deferTerminal = false)
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
            Ownership = request.Ownership,
            Status = ImportStatus.Processing,
            CompletedAt = null,
            ErrorMessage = null,
            ErrorCode = null,
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
            Ownership = request.Ownership,
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
        Stream? materializedStream = null;
        ImportScratchLease? scratchLease = null;
        string? materializedTemp = null;
        ICloudFileStorage? stagedCloud = null;
        string? stagedCloudFileId = null;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            IJobExecutionLeaseAuthority? executionAuthority = null;
            JobSecurityContext? validatedExecution = null;
            if (job.Audit.SubmitterSecurityContext is { } submitted &&
                (submitted.AgentCredential is not null || submitted.ExecutionLease is not null))
            {
                if (submitted.AgentCredential is null || submitted.ExecutionLease is null)
                    throw new UnauthorizedAccessException("A durable agent import requires an accepted job-specific execution lease.");
                executionAuthority = scope.ServiceProvider.GetService<IJobExecutionLeaseAuthority>()
                    ?? throw new UnauthorizedAccessException("Current job execution authority is unavailable.");
                validatedExecution = await executionAuthority.RevalidateForExecutionAsync(job, cancellationToken).ConfigureAwait(false);
            }
            var clock = scope.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
            var deadline = validatedExecution?.CredentialExpiresAt;
            if (validatedExecution is not null && (deadline is null || deadline <= clock.GetUtcNow()))
                throw new UnauthorizedAccessException("The accepted job execution lease has expired.");
            using var executionExpiry = deadline is { } expiresAt
                ? new CancellationTokenSource(expiresAt - clock.GetUtcNow(), clock) : null;
            using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                executionExpiry?.Token ?? CancellationToken.None);
            cancellationToken = executionCancellation.Token;
            try
            {
                var active = request;
                if (active.UsesObjectStore)
                {
                    var reader = scope.ServiceProvider.GetRequiredService<IImportObjectStoreReader>();
                    var cloud = scope.ServiceProvider.GetService<ICloudFileStorage>();
                    var storeOptions = scope.ServiceProvider
                        .GetRequiredService<Microsoft.Extensions.Options.IOptions<ImportObjectStoreOptions>>().Value;
                    var materialized = await ImportObjectStoreMaterializer.MaterializeAsync(
                        active,
                        reader,
                        cloud,
                        storeOptions,
                        cancellationToken,
                        fileSize > 0 ? fileSize : null).ConfigureAwait(false);
                    active = materialized.Request;
                    if (materialized.DisposeStream)
                    {
                        materializedStream = active.FileStream;
                    }

                    if (materialized.DeleteLocalFile)
                    {
                        materializedTemp = active.LocalFilePath;
                    }

                    if (materialized.DeleteCloudFile && cloud is not null)
                    {
                        stagedCloud = cloud;
                        stagedCloudFileId = active.CloudFileId;
                    }
                }

                var importService = scope.ServiceProvider.GetRequiredService<IFileImportService>();
                if (active.StagedSource is { } stagedSource)
                {
                    var cloud = scope.ServiceProvider.GetRequiredService<ImportStagedSourceStorage>().Storage;
                    scratchLease = await StagedImportSourceMaterializer.OpenAsync(stagedSource, cloud,
                        importService.Limits.MaxWorkerScratchBytes, scope.ServiceProvider.GetService<ImportScratchStorage>(), cancellationToken).ConfigureAwait(false);
                    active = active with { CloudFileId = null, FileStream = scratchLease.Content };
                }

                // Materialization can await remote storage. Revalidate the actual accepted
                // job after all source/scratch work, immediately before importer access.
                if (executionAuthority is not null)
                    validatedExecution = await executionAuthority.RevalidateForExecutionAsync(job, cancellationToken).ConfigureAwait(false);
                using var executionScope = validatedExecution is null ? null : JobSecurityScope.BeginExecution(job, validatedExecution);
                cancellationToken.ThrowIfCancellationRequested();
                result = await importService.ImportFileAsync(active, progress, cancellationToken).ConfigureAwait(false);

                if (result.Success)
                {
                    // None, not the job token: a committed import still refreshes its snapshot when
                    // the worker token is already cancelled (trunk #6256).
                    await ImportedSnapshotRefresher.RefreshAsync(
                        scope.ServiceProvider,
                        request.TargetSchema,
                        request.TableName,
                        ex => Log.SnapshotRefreshFailed(logger, jobId, ex),
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                if (scratchLease is not null)
                {
                    try
                    {
                        await scratchLease.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupEx) when (cleanupEx is not OutOfMemoryException)
                    {
                        // Cleanup cannot change the truth of a committed import. The
                        // unresolved reservation remains charged for reconciliation.
                        Log.ScratchCleanupDeferred(logger, jobId, cleanupEx);
                    }
                }
                if (materializedStream is not null)
                {
                    await materializedStream.DisposeAsync().ConfigureAwait(false);
                }

                if (materializedTemp is not null)
                {
                    try
                    {
                        File.Delete(materializedTemp);
                    }
                    catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
                    {
                        Log.ImportFaulted(logger, jobId, cleanupEx);
                    }
                }

                if (stagedCloud is not null && !string.IsNullOrWhiteSpace(stagedCloudFileId))
                {
                    try
                    {
                        // None so a cancelled import still deletes the staged copy.
                        await stagedCloud.DeleteAsync(stagedCloudFileId, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception cleanupEx) when (cleanupEx is not OutOfMemoryException)
                    {
                        Log.ImportFaulted(logger, jobId, cleanupEx);
                    }
                }
            }
        }
        catch (ImportObjectStoreException ex) when (!deferTerminal)
        {
            Log.ImportFaulted(logger, jobId, ex);
            await writer.CloseAndDrainAsync().ConfigureAwait(false);
            var failed = ImportResult.CreateFailure(
                request.TableName,
                SupportedFileFormat.GeoJson,
                SafeImportFailureMessage,
                errorCode: ex.Code);
            await WriteTerminalAsync(jobId, processing, ImportStatus.Failed, failed, SafeImportFailureMessage)
                .ConfigureAwait(false);
            return NonRetryableFailure(SafeImportFailureMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Operator cancel, timeout or worker shutdown: JobExecutionService owns the durable
            // outcome (cancelled, failed or handed back for retry), and the job-status read
            // projects a terminal execution record onto this document.
            await writer.CloseAndDrainAsync().ConfigureAwait(false);
            if (!deferTerminal && licenseCancellation.IsCancellationRequested)
            {
                // Parity with the in-process runner: a partial import stopped by licence expiry is a
                // terminal failure, never a retry.
                await WriteTerminalAsync(jobId, processing, ImportStatus.Failed, null, LicenseExpiredMessage).ConfigureAwait(false);
                return NonRetryableFailure(LicenseExpiredMessage);
            }

            throw;
        }
        catch (Exception ex) when (!deferTerminal && ex is not OutOfMemoryException)
        {
            // Deliberately broad: any importer fault is a failed import. The detail is logged;
            // the job document carries the same safe message the in-process runner reports.
            Log.ImportFaulted(logger, jobId, ex);
            await writer.CloseAndDrainAsync().ConfigureAwait(false);
            await WriteTerminalAsync(jobId, processing, ImportStatus.Failed, null, SafeImportFailureMessage).ConfigureAwait(false);
            return NonRetryableFailure(SafeImportFailureMessage);
        }

        finally
        {
            await writer.CloseAndDrainAsync().ConfigureAwait(false);
        }

        if (!result.Success)
        {
            Log.ImportFailed(logger, jobId, result.ErrorMessage ?? "unknown");
            var errorMessage = result.ErrorCode == ImportTableOwnershipException.Code
                ? ImportTableOwnershipException.CreateSafeMessage(result.TableName)
                : SafeImportFailureMessage;
            if (!deferTerminal)
                await WriteTerminalAsync(jobId, processing, ImportStatus.Failed, result, errorMessage).ConfigureAwait(false);
            return NonRetryableFailure(errorMessage, result.Warnings);
        }

        if (!deferTerminal)
        {
            await WriteTerminalAsync(jobId, processing, ImportStatus.Completed, result, result.ErrorMessage).ConfigureAwait(false);
            await context.ReportProgressAsync(100, "Import completed", CancellationToken.None).ConfigureAwait(false);
        }
        if (!deferTerminal) Log.ImportCompleted(logger, jobId, request.TableName, result.FeatureCount);
        return JobExecutionResult.Succeeded(result.Warnings) with
        {
            FileImportOutput = !string.IsNullOrWhiteSpace(result.PhysicalTableName)
                && !string.IsNullOrWhiteSpace(result.Schema) && result.FeatureCount >= 0
                ? new FileImportOutputReceipt
                {
                    PhysicalTableName = result.PhysicalTableName,
                    Schema = result.Schema,
                    FeatureCount = result.FeatureCount,
                    DetectedSrid = result.DetectedSrid,
                }
                : null,
        };
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
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        var current = await progressStore.GetProgressAsync<ImportProgress>(jobId, cancellationToken).ConfigureAwait(false)
            ?? fallback;
        var terminal = current with
        {
            Status = status,
            FeaturesProcessed = result?.FeatureCount ?? current.FeaturesProcessed,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = errorMessage,
            ErrorCode = result?.ErrorCode,
            Warnings = result?.Warnings ?? current.Warnings,
            DetectedSrid = result?.DetectedSrid ?? current.DetectedSrid,
            PhysicalTableName = status == ImportStatus.Completed && result?.Success == true ? result.PhysicalTableName : null,
            Schema = status == ImportStatus.Completed && result?.Success == true ? result.Schema : null,
            CurrentPhase = status == ImportStatus.Completed ? "Import completed" : "Import failed",
            Runner = fallback.Runner,
            InstanceId = fallback.InstanceId,
        };
        await progressStore.SetProgressAsync(jobId, terminal, ProgressRetention, cancellationToken).ConfigureAwait(false);
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
            proposalId: request.ProposalId,
            ownership: request.Ownership);

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

        [LoggerMessage(7487, LogLevel.Warning, "File-import job {JobId} requires scratch cleanup reconciliation")]
        public static partial void ScratchCleanupDeferred(ILogger logger, string jobId, Exception exception);
    }
}
