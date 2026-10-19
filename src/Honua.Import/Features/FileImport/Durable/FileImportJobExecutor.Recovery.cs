// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.ExceptionServices;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;

namespace Honua.Import.FileImport.Durable;

internal sealed partial class FileImportJobExecutor
{
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(30);

    public async Task<JobExecutionResult> ExecuteAsync(ExecutionJobRecord job,
        IJobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);
        if (job.Audit.SubmitterSecurityContext is not { AgentCredential: not null, ExecutionLease: not null } submitted)
            return await ExecuteCoreAsync(job, context, cancellationToken).ConfigureAwait(false);

        if (context.OperationId != job.OperationId || job.Spec.Kind != ExecutionJobKind.FileImport
            || submitted.ExecutionLease.JobId != job.OperationId
            || !FileImportJobParameters.TryParse(job.Spec.Parameters, out var request, out var bytes, out _)
            || request!.Acceptance?.OperationId != job.OperationId)
            throw new InvalidOperationException("The accepted import does not match the trusted worker context.");

        // This is internal reconciliation of an already committed fact. It restores no
        // execution principal and provides no external observation authority. Expired or
        // revoked executors cannot cause another source read or mutation through this path.
        var intent = JobExecutionLeaseIntentFingerprint.Compute(job);
        await using var scope = scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IExecutionJobStore>();
        var receipts = scope.ServiceProvider.GetRequiredService<IImportCommittedResultReader>();
        var committed = await ReadCommittedAsync().ConfigureAwait(false);
        if (committed is not null)
            return await CompleteCommittedAsync(committed).ConfigureAwait(false);

        JobExecutionResult? execution = null;
        ExceptionDispatchInfo? fault = null;
        try
        {
            execution = await ExecuteCoreAsync(job, context, cancellationToken, deferTerminal: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            fault = ExceptionDispatchInfo.Capture(ex);
        }

        // A lost COMMIT response or a cleanup/cancellation exception cannot tell us whether
        // the destination changed. Read its atomic receipt using an independent bounded
        // token. Failure to read is an unknown outcome, never permission to retry a write.
        committed = await ReadCommittedAsync().ConfigureAwait(false);
        if (committed is not null)
            return await CompleteCommittedAsync(committed).ConfigureAwait(false);
        fault?.Throw();
        if (execution is null || execution.Status == ExecutionJobStatus.Succeeded)
            throw new InvalidOperationException("The accepted import has no durable commit receipt.");

        using var failedCompletion = new CancellationTokenSource(RecoveryTimeout);
        await WriteTerminalAsync(job.OperationId, CreateInitialProgress(job.OperationId, request, bytes),
            ImportStatus.Failed, null, execution.ErrorMessage, failedCompletion.Token).ConfigureAwait(false);
        return execution;

        async Task<ImportResult?> ReadCommittedAsync()
        {
            using var recovery = new CancellationTokenSource(RecoveryTimeout);
            RequireSameJob(await jobs.GetAsync(job.OperationId, recovery.Token).ConfigureAwait(false));
            var receipt = await receipts.TryReadAsync(job.OperationId, intent, recovery.Token).ConfigureAwait(false);
            RequireSameJob(await jobs.GetAsync(job.OperationId, recovery.Token).ConfigureAwait(false));
            return receipt;
        }

        void RequireSameJob(ExecutionJobRecord? current)
        {
            if (current is null || current.OperationId != job.OperationId
                || current.Audit.SubmitterSecurityContext?.ExecutionLease != submitted.ExecutionLease
                || JobExecutionLeaseIntentFingerprint.Compute(current) != intent)
                throw new InvalidOperationException("The stored import intent changed during commit recovery.");
        }

        async Task<JobExecutionResult> CompleteCommittedAsync(ImportResult result)
        {
            if (!result.Success || result.TableName != request.TableName || string.IsNullOrWhiteSpace(result.PhysicalTableName)
                || string.IsNullOrWhiteSpace(result.Schema) || result.FeatureCount <= 0)
                throw new InvalidOperationException("The import commit receipt is invalid.");

            // The durable context retains worker/attempt CAS fences, including after a
            // cancellation. Only an actual database receipt can set this completion flag.
            using var completion = new CancellationTokenSource(RecoveryTimeout);
            await context.RecordCommittedEffectAsync($"honua-import-commit:{job.OperationId}:{intent}",
                completion.Token).ConfigureAwait(false);
            try
            {
                await WriteTerminalAsync(job.OperationId, CreateInitialProgress(job.OperationId, request, bytes),
                    ImportStatus.Completed, result, result.ErrorMessage, completion.Token).ConfigureAwait(false);
                await context.ReportProgressAsync(100, "Import completed", completion.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Progress is a projection. The durable completion below remains truthful
                // even when a subscriber, cache, or late cancellation refuses a notification.
                Log.ImportFaulted(logger, job.OperationId, ex);
            }
            Log.ImportCompleted(logger, job.OperationId, request.TableName, result.FeatureCount);
            return JobExecutionResult.Succeeded(result.Warnings) with
            {
                CompletedWithCommittedEffects = true,
                FileImportOutput = new FileImportOutputReceipt
                {
                    PhysicalTableName = result.PhysicalTableName,
                    Schema = result.Schema,
                    FeatureCount = result.FeatureCount,
                    DetectedSrid = result.DetectedSrid
                }
            };
        }
    }
}
