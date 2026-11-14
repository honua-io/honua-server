// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Temporal.Domain;
using Honua.Infrastructure.Authentication;
using Honua.ControlPlane;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Progress;
using Honua.Infrastructure;
using Microsoft.AspNetCore.Mvc;

using Honua.Server.Features.Admin.TileOperations;

namespace Honua.Server.Features.Admin;

internal static partial class OperationsProgressEndpoints
{
    private const int MaxCancelCompareAndSetAttempts = 3;

    public static void MapOperationsProgressEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/operations")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Operations Progress")
            .WithDescription("Unified operation progress tracking")
            .RequireAdminAuthorization();

        // Get operation status by ID
        _ = group.MapGet("/{operationId}", HandleGetOperationStatus)
            .WithName("GetOperationStatus")
            .WithSummary("Get the status of any operation by ID")
            .WithDescription("Returns progress information for any tracked operation type");

        // Cancel operation
        _ = group.MapPost("/{operationId}/cancel", HandleCancelOperation)
            .WithName("CancelOperation")
            .WithSummary("Cancel a running operation")
            .WithDescription("Attempts to cancel an operation that is currently queued or processing");

        // List active operations by type
        _ = group.MapGet("/active", HandleListActiveOperations)
            .WithName("ListActiveOperations")
            .WithSummary("List all active operations")
            .WithDescription("Returns all currently active operations, optionally filtered by type");

        // Get operations by type
        _ = group.MapGet("/type/{operationType}", HandleGetOperationsByType)
            .WithName("GetOperationsByType")
            .WithSummary("Get all operations of a specific type")
            .WithDescription("Returns all operations (active and completed) of the specified type");
    }

    private static async Task<IResult> HandleOrchestrationCancelAsync(
        string operationId,
        IOperationProgress progress,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var coordinator = httpContext.RequestServices.GetService<IWorkflowCancellationCoordinator>();
        if (coordinator is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status503ServiceUnavailable,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                "Orchestration engine is not available to process cancellation");
        }

        var outcome = await coordinator.CancelRunAsync(operationId, cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            WorkflowCancellationOutcome.NotFound => ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status404NotFound,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                $"Workflow run '{operationId}' was not found"),
            WorkflowCancellationOutcome.AlreadyTerminal => ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status409Conflict,
                "Conflict",
                $"Workflow run '{operationId}' already reached a terminal state"),
            WorkflowCancellationOutcome.LeaseContention => ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status409Conflict,
                "Conflict",
                $"Workflow run '{operationId}' is currently being reconciled; retry the cancel request shortly"),
            _ => Results.Json(
                new CancelOperationResponse
                {
                    OperationId = operationId,
                    Message = "Operation cancellation requested",
                    Type = progress.Type
                },
                OperationsProgressJsonContext.Default.CancelOperationResponse)
        };
    }

    private static async Task<IResult?> TryReconcileDurableCancelAsync(
        HttpContext httpContext,
        ExecutionJobRecord? executionJob,
        IUniversalProgressStore progressStore,
        CancellationToken cancellationToken)
    {
        var jobStore = httpContext.RequestServices.GetService<IExecutionJobStore>();
        if (jobStore == null)
        {
            return null;
        }

        if (executionJob == null)
        {
            return null;
        }

        var operationId = executionJob.OperationId;
        if (executionJob.Status.IsTerminal())
        {
            if (executionJob.Status == ExecutionJobStatus.Cancelled)
            {
                await TryRemoveJobQueueEntryAsync(httpContext, operationId, cancellationToken).ConfigureAwait(false);
                return null;
            }

            await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                progressStore, executionJob, TimeSpan.FromDays(7), cancellationToken: cancellationToken).ConfigureAwait(false);

            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status409Conflict,
                "Conflict",
                $"Operation '{operationId}' reached terminal state '{executionJob.Status}' in the durable job store");
        }

        if (!string.Equals(executionJob.Spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal))
        {
            var effectiveJob = executionJob;
            if (!executionJob.CancellationRequestedAt.HasValue)
            {
                var backends = httpContext.RequestServices.GetService<IEnumerable<IBatchComputeBackend>>();
                var backend = backends?.Resolve(executionJob.Spec.Backend, executionJob.Spec.TargetKind);
                if (backend == null)
                {
                    return ProblemDetailsHelpers.CreateAdminProblem(
                        StatusCodes.Status409Conflict,
                        "Conflict",
                        $"No batch compute backend registered for '{executionJob.Spec.Backend}' ({executionJob.Spec.TargetKind}). Cannot cancel remote job locally.");
                }

                var capabilities = await backend.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
                if (!capabilities.SupportsCancellation)
                {
                    return ProblemDetailsHelpers.CreateAdminProblem(
                        StatusCodes.Status409Conflict,
                        "Conflict",
                        $"Backend '{executionJob.Spec.Backend}' does not support cancellation.");
                }

                var now = DateTimeOffset.UtcNow;
                var requested = executionJob with
                {
                    CancellationRequestedAt = now,
                    UpdatedAt = now
                };
                if (await jobStore.TrySetAsync(requested, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    effectiveJob = requested;
                }
                else
                {
                    var fresh = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
                    if (fresh == null)
                    {
                        return ProblemDetailsHelpers.CreateAdminProblem(
                            StatusCodes.Status409Conflict,
                            "Conflict",
                            $"Operation '{operationId}' was deleted before cancellation could be applied");
                    }

                    if (fresh.Status == ExecutionJobStatus.Cancelled)
                    {
                        // Already cancelled — idempotent success.
                        effectiveJob = fresh;
                    }
                    else if (fresh.Status.IsTerminal())
                    {
                        await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                            progressStore, fresh, TimeSpan.FromDays(7), cancellationToken: cancellationToken).ConfigureAwait(false);

                        return ProblemDetailsHelpers.CreateAdminProblem(
                            StatusCodes.Status409Conflict,
                            "Conflict",
                            $"Operation '{operationId}' reached terminal state '{fresh.Status}' in the durable job store before cancellation could be applied");
                    }
                    else if (fresh.CancellationRequestedAt.HasValue)
                    {
                        // Another path already set the cancellation signal — idempotent success.
                        effectiveJob = fresh;
                    }
                    else
                    {
                        var retryNow = DateTimeOffset.UtcNow;
                        var retryRequested = fresh with
                        {
                            CancellationRequestedAt = retryNow,
                            UpdatedAt = retryNow
                        };
                        if (!await jobStore.TrySetAsync(retryRequested, cancellationToken: cancellationToken).ConfigureAwait(false))
                        {
                            return ProblemDetailsHelpers.CreateAdminProblem(
                                StatusCodes.Status409Conflict,
                                "Conflict",
                                $"Operation '{operationId}' cancellation could not be confirmed after retries.");
                        }

                        effectiveJob = retryRequested;
                    }
                }
            }

            if (!effectiveJob.Status.IsTerminal())
            {
                await ExecutionJobSubmissionHelper.BridgeExecutionJobProgressAsync(
                    progressStore, effectiveJob, TimeSpan.FromDays(7), cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        var cancelOutcome = await ExecutionJobCancellationHelper.TryApplyAsync(
            jobStore,
            operationId,
            executionJob,
            "Cancelled",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        switch (cancelOutcome.State)
        {
            case ExecutionJobCancellationState.Cancelled:
                await BridgeCancelledExecutionAsync(httpContext, cancelOutcome, cancellationToken).ConfigureAwait(false);

                await TryRemoveJobQueueEntryAsync(httpContext, operationId, cancellationToken).ConfigureAwait(false);
                return null;
            case ExecutionJobCancellationState.CancellationRequested:
                if (cancelOutcome.Job != null && !cancelOutcome.Job.Status.IsTerminal())
                {
                    await ExecutionJobSubmissionHelper.BridgeExecutionJobProgressAsync(
                        progressStore, cancelOutcome.Job, TimeSpan.FromDays(7), cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                return null;
            case ExecutionJobCancellationState.Missing:
                return null;
            case ExecutionJobCancellationState.TerminalConflict:
                if (cancelOutcome.Job?.Status == ExecutionJobStatus.Cancelled)
                {
                    return null;
                }

                if (cancelOutcome.Job != null)
                {
                    await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                        progressStore, cancelOutcome.Job, TimeSpan.FromDays(7), cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status409Conflict,
                    "Conflict",
                    $"Operation '{operationId}' reached terminal state '{cancelOutcome.Job?.Status}' in the durable job store");
            case ExecutionJobCancellationState.Unconfirmed:
                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status409Conflict,
                    "Conflict",
                    $"Operation '{operationId}' cancellation could not be confirmed after retries.");
            default:
                return null;
        }
    }

    private static async Task TryRemoveJobQueueEntryAsync(
        HttpContext httpContext,
        string operationId,
        CancellationToken cancellationToken)
    {
        var jobQueue = httpContext.RequestServices.GetService<IJobQueue>();
        if (jobQueue == null)
        {
            return;
        }

        try
        {
            await jobQueue.RemoveAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional broad catch: best-effort job-queue cleanup after a cancel. A
            // failure here must not fail the cancel request; the stale-claim reconciler
            // repairs any leftover queue entry.
            Log.QueueRemovalFailed(
                httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(OperationsProgressEndpoints).FullName!),
                operationId,
                ex);
        }
    }

    internal static partial class Log
    {
        [LoggerMessage(9080, LogLevel.Warning, "Admin cancel queue cleanup failed for operation {OperationId}; stale-claim reconciler will repair")]
        public static partial void QueueRemovalFailed(ILogger logger, string operationId, Exception exception);
    }

    private static void NormalizeCanonicalProperty(JsonObject operation, string propertyName, JsonNode? value)
    {
        var duplicateKeys = operation
            .Where(property => string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            .Select(property => property.Key)
            .ToList();

        foreach (var duplicateKey in duplicateKeys)
        {
            operation.Remove(duplicateKey);
        }

        operation[propertyName] = value;
    }

    private static async Task<IResult> HandleCancelOperation(
        string operationId,
        [FromServices] IUniversalProgressStore progressStore,
        [FromServices] IEnumerable<IJobCancellationNotifier> cancellationNotifiers,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(operationId))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "Operation ID is required");
        }

        var jobStore = httpContext.RequestServices.GetService<IExecutionJobStore>();
        var progressReader = new ExecutionJobProgressReader(progressStore, jobStore);
        var progress = await progressReader.ReadAsync(operationId, cancellationToken);

        if (progress == null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status404NotFound,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                $"Operation '{operationId}' not found");
        }

        if (progress.Status is OperationStatus.Completed or OperationStatus.Failed)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status409Conflict,
                "Conflict",
                $"Operation '{operationId}' reached terminal state '{progress.Status}' before cancellation could be applied");
        }

        if (progress.Status is OperationStatus.Cancelled)
        {
            return await AlreadyCancelledAsync(httpContext, progress, cancellationToken).ConfigureAwait(false);
        }

        if (progress is not ICancellableOperationProgress cancellableProgress)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                $"Operation type '{progress.Type}' does not support cancellation");
        }

        // Orchestration cancel needs to update the authoritative WorkflowRun record so the
        // reconcile loop can cascade cancellation to the underlying queued/running jobs.
        // A progress-only cancel would be silently overwritten on the next reconcile tick.
        if (progress.Type == OperationType.Orchestration)
        {
            return await HandleOrchestrationCancelAsync(
                operationId,
                progress,
                httpContext,
                cancellationToken).ConfigureAwait(false);
        }

        // Persist local cancellation before the process-only signal so takeover after a
        // worker crash observes the accepted request. The worker still owns its terminal state.
        ExecutionJobRecord? cancellationTarget = null;
        if (jobStore != null)
        {
            cancellationTarget = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (cancellationTarget?.Spec.Backend == LocalBatchComputeBackend.BackendId)
            {
                return await CancelLocalExecutionAsync(
                    httpContext, (cancellationTarget, progress), cancellationToken).ConfigureAwait(false);
            }
        }

        // Signal in-flight job processors so they can begin aborting work.
        // When a notifier confirms the job is in-flight (returns true), the
        // background worker owns the terminal state transition — it will persist
        // Cancelled (if interrupted) or Completed (if rendering already finished).
        // Writing Cancelled here would race with the worker's Completed write and
        // could hide a successfully rendered result.
        var workerOwnsTerminalState = cancellationTarget != null
            ? ExecutionJobCancellationTokens.CancelAttempt(cancellationNotifiers, cancellationTarget)
            : cancellationNotifiers.CancelAny(operationId);

        if (workerOwnsTerminalState)
        {
            // The worker received the signal and will persist the final state.
            // Return success immediately — the caller should poll for the
            // terminal state via the job-status endpoint.
            var delegated = new CancelOperationResponse
            {
                OperationId = operationId,
                Message = "Operation cancellation requested",
                Type = progress.Type
            };
            return Results.Json(delegated, OperationsProgressJsonContext.Default.CancelOperationResponse);
        }

        // No worker claimed the job (not yet dequeued, already finished, or
        // an operation type without a background worker).  Write Cancelled
        // directly after a re-read to narrow the TOCTOU window.
        var latestProgress = await progressReader.ReadAsync(operationId, cancellationToken);
        if (latestProgress is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status404NotFound,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                $"Operation '{operationId}' was not found on re-read (it may have expired)");
        }

        if (latestProgress.Status is OperationStatus.Completed or OperationStatus.Failed)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status409Conflict,
                "Conflict",
                $"Operation '{operationId}' reached terminal state '{latestProgress.Status}' before cancellation could be applied");
        }

        if (latestProgress.Status is OperationStatus.Cancelled)
        {
            return await AlreadyCancelledAsync(httpContext, latestProgress, cancellationToken).ConfigureAwait(false);
        }

        if (latestProgress is not ICancellableOperationProgress latestCancellable)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                $"Operation type '{latestProgress.Type}' does not support cancellation");
        }

        // If this operation is backed by a durable execution job, synchronize the
        // cancellation to the authoritative job store and remove from the queue so
        // the job does not remain claimable after the admin cancel.
        if (jobStore != null)
        {
            var executionJob = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (executionJob != null)
            {
                if (executionJob.Status.IsTerminal())
                {
                    if (executionJob.Status == ExecutionJobStatus.Cancelled)
                    {
                        await TryRemoveJobQueueEntryAsync(httpContext, operationId, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                            progressStore, executionJob, TimeSpan.FromDays(7), cancellationToken: cancellationToken).ConfigureAwait(false);

                        return ProblemDetailsHelpers.CreateAdminProblem(
                            StatusCodes.Status409Conflict,
                            "Conflict",
                            $"Operation '{operationId}' reached terminal state '{executionJob.Status}' in the durable job store before cancellation could be applied");
                    }
                }
                else if (!string.Equals(executionJob.Spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal))
                {
                    var backends = httpContext.RequestServices.GetService<IEnumerable<IBatchComputeBackend>>();
                    var backend = backends?.Resolve(executionJob.Spec.Backend, executionJob.Spec.TargetKind);
                    if (backend == null)
                    {
                        return ProblemDetailsHelpers.CreateAdminProblem(
                            StatusCodes.Status409Conflict,
                            "Conflict",
                            $"No batch compute backend registered for '{executionJob.Spec.Backend}' ({executionJob.Spec.TargetKind}). Cannot cancel remote job locally.");
                    }

                    var capabilities = await backend.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
                    if (!capabilities.SupportsCancellation)
                    {
                        return ProblemDetailsHelpers.CreateAdminProblem(
                            StatusCodes.Status409Conflict,
                            "Conflict",
                            $"Backend '{executionJob.Spec.Backend}' does not support cancellation.");
                    }

                    if (executionJob.Status == ExecutionJobStatus.Queued && !ExecutionJobCancellationHelper.HasSubmittedProviderMarker(executionJob))
                    {
                        var preResult = await ExecutionJobCancellationHelper.TryCancelPreSubmissionAsync(
                            jobStore, executionJob, cancellationToken).ConfigureAwait(false);

                        if (preResult.TerminalTransition is { Spec.Kind: ExecutionJobKind.TileCache } preSubmissionTileJob)
                        {
                            TileOperationMetrics.RecordJobEnded(preSubmissionTileJob);
                        }

                        switch (preResult.Outcome)
                        {
                            case PreSubmissionCancelOutcome.TerminalConflict:
                                await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                                    progressStore, preResult.Job!, TimeSpan.FromDays(7), cancellationToken: cancellationToken).ConfigureAwait(false);
                                return ProblemDetailsHelpers.CreateAdminProblem(
                                    StatusCodes.Status409Conflict,
                                    "Conflict",
                                    $"Operation '{operationId}' reached terminal state '{preResult.Job!.Status}' before pre-submission cancellation could be applied");
                            case PreSubmissionCancelOutcome.Missing:
                                return ProblemDetailsHelpers.CreateAdminProblem(
                                    StatusCodes.Status409Conflict,
                                    "Conflict",
                                    $"Operation '{operationId}' was deleted before pre-submission cancellation could be applied");
                            case PreSubmissionCancelOutcome.Unconfirmed:
                                return ProblemDetailsHelpers.CreateAdminProblem(
                                    StatusCodes.Status409Conflict,
                                    "Conflict",
                                    $"Operation '{operationId}' pre-submission cancellation could not be confirmed.");
                        }

                        await TryRemoveJobQueueEntryAsync(httpContext, operationId, cancellationToken).ConfigureAwait(false);
                        await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                            progressStore, preResult.Job!, TimeSpan.FromDays(7), cancellationToken: cancellationToken).ConfigureAwait(false);
                        var localResponse = new CancelOperationResponse
                        {
                            OperationId = operationId,
                            Message = "Operation cancellation requested",
                            Type = progress.Type
                        };
                        return Results.Json(localResponse, OperationsProgressJsonContext.Default.CancelOperationResponse);
                    }

                    // Stamp CancellationRequestedAt before the remote cancel so a concurrent
                    // caller that races ahead and sees the provider job already gone maps
                    // NotFound → Cancelled rather than reclassifying the record as Failed.
                    var stampResult = await ExecutionJobCancellationHelper.TryStampRemoteCancelRequestedAtAsync(
                        jobStore, executionJob, cancellationToken: cancellationToken).ConfigureAwait(false);
                    switch (stampResult.Outcome)
                    {
                        case RemoteCancelStampOutcome.Missing:
                            return ProblemDetailsHelpers.CreateAdminProblem(
                                StatusCodes.Status409Conflict,
                                "Conflict",
                                $"Operation '{operationId}' was deleted before remote cancellation could be applied");
                        case RemoteCancelStampOutcome.TerminalConflict:
                            await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                                progressStore, stampResult.Job!, TimeSpan.FromDays(7), cancellationToken: cancellationToken).ConfigureAwait(false);
                            return ProblemDetailsHelpers.CreateAdminProblem(
                                StatusCodes.Status409Conflict,
                                "Conflict",
                                $"Operation '{operationId}' reached terminal state '{stampResult.TerminalStatus ?? stampResult.Job!.Status}' in the durable job store before remote cancellation could be applied");
                        case RemoteCancelStampOutcome.Unconfirmed:
                            return ProblemDetailsHelpers.CreateAdminProblem(
                                StatusCodes.Status409Conflict,
                                "Conflict",
                                $"Operation '{operationId}' remote cancellation could not be confirmed after retries.");
                    }

                    var stampedJob = stampResult.Job!;
                    var observation = await backend.CancelAsync(stampedJob, cancellationToken).ConfigureAwait(false);
                    var applyResult = await ExecutionJobCancellationHelper.TryApplyBackendCancelAsync(jobStore, (stampedJob, observation), cancellationToken).ConfigureAwait(false);

                    if (applyResult.TerminalTransition is { Spec.Kind: ExecutionJobKind.TileCache } backendTileJob)
                    {
                        TileOperationMetrics.RecordJobEnded(backendTileJob);
                    }

                    switch (applyResult.Outcome)
                    {
                        case BackendCancelApplyOutcome.Missing:
                            return ProblemDetailsHelpers.CreateAdminProblem(
                                StatusCodes.Status409Conflict,
                                "Conflict",
                                $"Operation '{operationId}' was deleted before remote cancellation could be applied");
                        case BackendCancelApplyOutcome.TerminalConflict:
                            await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                                progressStore, applyResult.Job!, TimeSpan.FromDays(7), cancellationToken: cancellationToken).ConfigureAwait(false);

                            return ProblemDetailsHelpers.CreateAdminProblem(
                                StatusCodes.Status409Conflict,
                                "Conflict",
                                $"Operation '{operationId}' reached terminal state '{applyResult.TerminalStatus ?? applyResult.Job!.Status}' in the durable job store before remote cancellation could be applied");
                        case BackendCancelApplyOutcome.Unconfirmed:
                            return ProblemDetailsHelpers.CreateAdminProblem(
                                StatusCodes.Status409Conflict,
                                "Conflict",
                                $"Operation '{operationId}' remote cancellation could not be confirmed after retries.");
                    }

                    var updated = applyResult.Job!;

                    if (updated.Status.IsTerminal())
                    {
                        await TryRemoveJobQueueEntryAsync(httpContext, operationId, cancellationToken).ConfigureAwait(false);
                    }

                    await ExecutionJobSubmissionHelper.BridgeExecutionJobProgressAsync(
                        progressStore, updated, TimeSpan.FromDays(7), cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (updated.Status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed)
                    {
                        return ProblemDetailsHelpers.CreateAdminProblem(
                            StatusCodes.Status409Conflict,
                            "Conflict",
                            $"Operation '{operationId}' reached terminal state '{updated.Status}' in the durable job store before remote cancellation could be applied");
                    }

                    var remoteResponse = new CancelOperationResponse
                    {
                        OperationId = operationId,
                        Message = "Operation cancellation requested",
                        Type = progress.Type
                    };
                    return Results.Json(remoteResponse, OperationsProgressJsonContext.Default.CancelOperationResponse);
                }
                else
                {
                    var cancelOutcome = await ExecutionJobCancellationHelper.TryApplyAsync(
                        jobStore,
                        operationId,
                        executionJob,
                        "Cancelled",
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    switch (cancelOutcome.State)
                    {
                        case ExecutionJobCancellationState.Cancelled:
                            await TryRemoveJobQueueEntryAsync(httpContext, operationId, cancellationToken).ConfigureAwait(false);
                            if (executionJob.Spec.Kind is ExecutionJobKind.Export or ExecutionJobKind.TileCache)
                            {
                                var cancelled = new CancelOperationResponse
                                {
                                    OperationId = operationId,
                                    Message = "Operation cancellation requested",
                                    Type = progress.Type
                                };
                                return Results.Json(cancelled, OperationsProgressJsonContext.Default.CancelOperationResponse);
                            }
                            break;
                        case ExecutionJobCancellationState.CancellationRequested:
                            var delegated = new CancelOperationResponse
                            {
                                OperationId = operationId,
                                Message = "Operation cancellation requested",
                                Type = progress.Type
                            };
                            return Results.Json(delegated, OperationsProgressJsonContext.Default.CancelOperationResponse);
                        case ExecutionJobCancellationState.TerminalConflict:
                            if (cancelOutcome.Job != null)
                            {
                                await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                                    progressStore, cancelOutcome.Job, TimeSpan.FromDays(7), cancellationToken: cancellationToken).ConfigureAwait(false);
                            }

                            return ProblemDetailsHelpers.CreateAdminProblem(
                                StatusCodes.Status409Conflict,
                                "Conflict",
                                $"Operation '{operationId}' reached terminal state '{cancelOutcome.Job?.Status}' in the durable job store before cancellation could be applied");
                        case ExecutionJobCancellationState.Missing:
                            return ProblemDetailsHelpers.CreateAdminProblem(
                                StatusCodes.Status409Conflict,
                                "Conflict",
                                $"Operation '{operationId}' was deleted before cancellation could be applied");
                        case ExecutionJobCancellationState.Unconfirmed:
                            return ProblemDetailsHelpers.CreateAdminProblem(
                                StatusCodes.Status409Conflict,
                                "Conflict",
                                $"Operation '{operationId}' cancellation could not be confirmed after retries.");
                        default:
                            throw new InvalidOperationException($"Unexpected durable cancellation outcome '{cancelOutcome.State}'.");
                    }
                }
            }
        }

        // Compare-and-set the Cancelled write so a worker's terminal Completed/Failed state that lands
        // concurrently can never be overwritten (the TOCTOU follow-on tracked from honua-server#1593).
        // Benign Queued <-> Processing transitions are retried; terminal transitions surface as 409.
        IOperationProgress observedProgress = latestProgress;
        ICancellableOperationProgress observedCancellable = latestCancellable;
        for (var attempt = 0; attempt < MaxCancelCompareAndSetAttempts; attempt++)
        {
            var cancelledProgress = observedCancellable.WithCancellation(DateTimeOffset.UtcNow, "Cancelled by user");
            var casResult = await progressStore.TrySetProgressAsync(new OperationProgressWrite(operationId, cancelledProgress, TimeSpan.FromDays(7)), observedProgress.Status, cancellationToken);

            if (casResult.Outcome == ProgressCompareAndSetOutcome.Updated)
            {
                var response = new CancelOperationResponse
                {
                    OperationId = operationId,
                    Message = "Operation cancellation requested",
                    Type = progress.Type
                };

                return Results.Json(response, OperationsProgressJsonContext.Default.CancelOperationResponse);
            }

            if (casResult.Outcome == ProgressCompareAndSetOutcome.NotFound)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status404NotFound,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                    $"Operation '{operationId}' was not found when cancellation was applied (it may have expired)");
            }

            var current = casResult.CurrentProgress!;
            if (current.Status is OperationStatus.Completed or OperationStatus.Failed)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status409Conflict,
                    "Conflict",
                    $"Operation '{operationId}' reached terminal state '{current.Status}' before cancellation could be applied");
            }

            if (current.Status is OperationStatus.Cancelled)
            {
                return await AlreadyCancelledAsync(httpContext, current, cancellationToken).ConfigureAwait(false);
            }

            if (current is not ICancellableOperationProgress currentCancellable)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status400BadRequest,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                    $"Operation type '{current.Type}' does not support cancellation");
            }

            observedProgress = current;
            observedCancellable = currentCancellable;
        }

        return ProblemDetailsHelpers.CreateAdminProblem(
            StatusCodes.Status409Conflict,
            "Conflict",
            $"Operation '{operationId}' status changed concurrently while cancellation was being applied; retry the cancel request");
    }

}
