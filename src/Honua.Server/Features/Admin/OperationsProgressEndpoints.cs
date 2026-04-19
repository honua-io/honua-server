// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Progress;
using Honua.Server.Features.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Unified operation progress endpoints for tracking any tracked operation type.
/// Replaces legacy progress endpoints with a single, consistent API.
/// </summary>
internal static partial class OperationsProgressEndpoints
{
    /// <summary>
    /// Map unified operation progress endpoints.
    /// </summary>
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

    /// <summary>
    /// Get the status of any operation by its ID.
    /// </summary>
    private static async Task<IResult> HandleGetOperationStatus(
        string operationId,
        [FromServices] IUniversalProgressStore progressStore,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(operationId))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "Operation ID is required");
        }

        var progress = await progressStore.GetProgressAsync(operationId, cancellationToken);

        if (progress == null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status404NotFound,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                $"Operation '{operationId}' not found");
        }

        return CreateOperationStatusResult(progress);
    }

    private static IResult CreateOperationStatusResult(IOperationProgress progress)
    {
        return progress switch
        {
            UploadProgress uploadProgress => Results.Json(uploadProgress, OperationsProgressJsonContext.Default.UploadProgress),
            ImportProgress importProgress => Results.Json(importProgress, OperationsProgressJsonContext.Default.ImportProgress),
            IngestProgress ingestProgress => Results.Json(ingestProgress, OperationsProgressJsonContext.Default.IngestProgress),
            GeoservicesImportProgress externalImportProgress => Results.Json(externalImportProgress, OperationsProgressJsonContext.Default.GeoservicesImportProgress),
            TileOperationProgress tileOperationProgress => Results.Json(tileOperationProgress, OperationsProgressJsonContext.Default.TileOperationProgress),
            ExportProgress exportProgress => Results.Json(exportProgress, OperationsProgressJsonContext.Default.ExportProgress),
            PrintProgress printProgress => Results.Json(printProgress, OperationsProgressJsonContext.Default.PrintProgress),
            RasterImportProgress rasterImportProgress => Results.Json(rasterImportProgress, OperationsProgressJsonContext.Default.RasterImportProgress),
            GeoprocessingProgress geoprocessingProgress => Results.Json(geoprocessingProgress, OperationsProgressJsonContext.Default.GeoprocessingProgress),
            PublishingProgress publishingProgress => Results.Json(publishingProgress, OperationsProgressJsonContext.Default.PublishingProgress),
            WorkflowProgress workflowProgress => Results.Json(workflowProgress, OperationsProgressJsonContext.Default.WorkflowProgress),
            DeploymentProgress deploymentProgress => Results.Json(deploymentProgress, OperationsProgressJsonContext.Default.DeploymentProgress),
            _ => Results.Json(progress, OperationsProgressJsonContext.Default.IOperationProgress)
        };
    }

    private static JsonElement SerializeOperationToElement(IOperationProgress progress)
    {
        var serialized = progress switch
        {
            UploadProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.UploadProgress),
            ImportProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.ImportProgress),
            IngestProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.IngestProgress),
            GeoservicesImportProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.GeoservicesImportProgress),
            TileOperationProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.TileOperationProgress),
            ExportProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.ExportProgress),
            PrintProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.PrintProgress),
            RasterImportProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.RasterImportProgress),
            GeoprocessingProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.GeoprocessingProgress),
            PublishingProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.PublishingProgress),
            WorkflowProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.WorkflowProgress),
            DeploymentProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.DeploymentProgress),
            _ => JsonSerializer.SerializeToElement(progress, OperationsProgressJsonContext.Default.IOperationProgress)
        };

        var operation = JsonNode.Parse(serialized.GetRawText())?.AsObject() ?? [];

        // List payloads need stable cross-operation keys for generic admin clients
        // while still preserving the type-specific fields from each concrete progress DTO.
        NormalizeCanonicalProperty(operation, "operationId", JsonValue.Create(progress.OperationId));
        NormalizeCanonicalProperty(operation, "type", JsonValue.Create((int)progress.Type));

        return JsonSerializer.SerializeToElement(operation);
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
        string operationId,
        IUniversalProgressStore progressStore,
        CancellationToken cancellationToken)
    {
        var jobStore = httpContext.RequestServices.GetService<IExecutionJobStore>();
        if (jobStore == null)
        {
            return null;
        }

        var executionJob = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (executionJob == null)
        {
            return null;
        }

        if (ExecutionJobCancellationHelper.IsTerminal(executionJob.Status))
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
                    else if (ExecutionJobCancellationHelper.IsTerminal(fresh.Status))
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

            if (!ExecutionJobCancellationHelper.IsTerminal(effectiveJob.Status))
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
                await TryRemoveJobQueueEntryAsync(httpContext, operationId, cancellationToken).ConfigureAwait(false);
                return null;
            case ExecutionJobCancellationState.CancellationRequested:
                if (cancelOutcome.Job != null && !ExecutionJobCancellationHelper.IsTerminal(cancelOutcome.Job.Status))
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
        catch (Exception ex)
        {
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
        var duplicateKeys = new List<string>();

        foreach (var property in operation)
        {
            if (string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                duplicateKeys.Add(property.Key);
            }
        }

        foreach (var duplicateKey in duplicateKeys)
        {
            operation.Remove(duplicateKey);
        }

        operation[propertyName] = value;
    }

    /// <summary>
    /// Cancel a running operation.
    /// </summary>
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

        var progress = await progressStore.GetProgressAsync(operationId, cancellationToken);

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
            var reconcileResult = await TryReconcileDurableCancelAsync(httpContext, operationId, progressStore, cancellationToken);
            if (reconcileResult != null)
            {
                return reconcileResult;
            }

            var alreadyCancelled = new CancelOperationResponse
            {
                OperationId = operationId,
                Message = "Operation cancellation requested",
                Type = progress.Type
            };
            return Results.Json(alreadyCancelled, OperationsProgressJsonContext.Default.CancelOperationResponse);
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

        // Signal in-flight job processors so they can begin aborting work.
        // When a notifier confirms the job is in-flight (returns true), the
        // background worker owns the terminal state transition — it will persist
        // Cancelled (if interrupted) or Completed (if rendering already finished).
        // Writing Cancelled here would race with the worker's Completed write and
        // could hide a successfully rendered result.
        var workerOwnsTerminalState = cancellationNotifiers.CancelAny(operationId);

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
        var latestProgress = await progressStore.GetProgressAsync(operationId, cancellationToken);
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
            var reconcileResult = await TryReconcileDurableCancelAsync(httpContext, operationId, progressStore, cancellationToken);
            if (reconcileResult != null)
            {
                return reconcileResult;
            }

            var alreadyCancelled = new CancelOperationResponse
            {
                OperationId = operationId,
                Message = "Operation cancellation requested",
                Type = progress.Type
            };
            return Results.Json(alreadyCancelled, OperationsProgressJsonContext.Default.CancelOperationResponse);
        }

        if (latestProgress is not ICancellableOperationProgress latestCancellable)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                $"Operation type '{latestProgress.Type}' does not support cancellation");
        }

        // TOCTOU note: a worker-backed job could complete between our re-read
        // and this write, causing Cancelled to overwrite Completed.  Worker-backed
        // operations (e.g. print jobs) mitigate this by re-asserting Completed
        // after their initial write.  A proper fix is compare-and-set semantics
        // in IUniversalProgressStore (tracked as follow-on).
        //
        // Narrow the TOCTOU window: re-read one final time immediately before
        // writing so a worker Completed write that landed between our earlier
        // re-read and this point is detected.
        var preWriteProgress = await progressStore.GetProgressAsync(operationId, cancellationToken);
        if (preWriteProgress?.Status is OperationStatus.Completed or OperationStatus.Failed)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status409Conflict,
                "Conflict",
                $"Operation '{operationId}' reached terminal state '{preWriteProgress.Status}' before cancellation could be applied");
        }

        // If this operation is backed by a durable execution job, synchronize the
        // cancellation to the authoritative job store and remove from the queue so
        // the job does not remain claimable after the admin cancel.
        var jobStore = httpContext.RequestServices.GetService<IExecutionJobStore>();
        if (jobStore != null)
        {
            var executionJob = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (executionJob != null)
            {
                if (ExecutionJobCancellationHelper.IsTerminal(executionJob.Status))
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

                    if (executionJob.Status == ExecutionJobStatus.Queued && !ExecutionJobCancellationHelper.WasSubmittedToProvider(executionJob))
                    {
                        var preResult = await ExecutionJobCancellationHelper.TryCancelPreSubmissionAsync(
                            jobStore, executionJob, cancellationToken).ConfigureAwait(false);

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

                    var observation = await backend.CancelAsync(executionJob, cancellationToken).ConfigureAwait(false);
                    var applyResult = await ExecutionJobCancellationHelper.TryApplyBackendCancelAsync(
                        jobStore, executionJob, observation, cancellationToken).ConfigureAwait(false);

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

                    if (ExecutionJobCancellationHelper.IsTerminal(updated.Status))
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

        var cancelledProgress = latestCancellable.WithCancellation(DateTimeOffset.UtcNow, "Cancelled by user");
        await progressStore.SetProgressAsync(operationId, cancelledProgress,
            TimeSpan.FromDays(7), cancellationToken);

        var response = new CancelOperationResponse
        {
            OperationId = operationId,
            Message = "Operation cancellation requested",
            Type = progress.Type
        };

        return Results.Json(response, OperationsProgressJsonContext.Default.CancelOperationResponse);
    }

    /// <summary>
    /// List all active operations, optionally filtered by type.
    /// </summary>
    private static async Task<IResult> HandleListActiveOperations(
        [FromServices] IUniversalProgressStore progressStore,
        string? type = null,
        CancellationToken cancellationToken = default)
    {
        OperationType? operationType = null;

        if (!string.IsNullOrEmpty(type))
        {
            if (!Enum.TryParse<OperationType>(type, true, out var parsedType))
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status400BadRequest,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                    $"Invalid operation type '{type}'. Valid types: {string.Join(", ", Enum.GetNames<OperationType>())}");
            }
            operationType = parsedType;
        }

        var operationIds = await progressStore.GetActiveOperationIdsAsync(operationType, cancellationToken);
        var operations = new List<JsonElement>();

        foreach (var operationId in operationIds)
        {
            var progress = await progressStore.GetProgressAsync(operationId, cancellationToken);
            if (progress != null &&
                progress.Status is OperationStatus.Queued or OperationStatus.Processing)
            {
                operations.Add(SerializeOperationToElement(progress));
            }
        }

        var response = new ActiveOperationsResponse
        {
            Operations = operations.ToArray(),
            TotalCount = operations.Count,
            FilteredByType = operationType
        };

        return Results.Json(response, OperationsProgressJsonContext.Default.ActiveOperationsResponse);
    }

    /// <summary>
    /// Get all operations of a specific type.
    /// </summary>
    private static async Task<IResult> HandleGetOperationsByType(
        string operationType,
        [FromServices] IUniversalProgressStore progressStore,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<OperationType>(operationType, true, out var parsedType))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                $"Invalid operation type '{operationType}'. Valid types: {string.Join(", ", Enum.GetNames<OperationType>())}");
        }

        var operationIds = await progressStore.GetActiveOperationIdsAsync(parsedType, cancellationToken);
        var operations = new List<JsonElement>(operationIds.Count);

        foreach (var operationId in operationIds)
        {
            var progress = await progressStore.GetProgressAsync(operationId, cancellationToken);
            if (progress != null && progress.Type == parsedType)
            {
                operations.Add(SerializeOperationToElement(progress));
            }
        }

        var response = new OperationsByTypeResponse
        {
            OperationType = parsedType,
            Operations = operations.ToArray(),
            TotalCount = operations.Count
        };

        return Results.Json(response, OperationsProgressJsonContext.Default.OperationsByTypeResponse);
    }
}

/// <summary>
/// Response for operation cancellation.
/// </summary>
internal sealed record CancelOperationResponse
{
    /// <summary>
    /// The operation ID that was cancelled.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Confirmation message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Type of operation that was cancelled.
    /// </summary>
    public required OperationType Type { get; init; }
}

/// <summary>
/// Response containing list of active operations.
/// </summary>
internal sealed record ActiveOperationsResponse
{
    /// <summary>
    /// List of active operations, each serialized with its concrete progress type
    /// plus stable operationId/type fields for generic clients.
    /// </summary>
    public required JsonElement[] Operations { get; init; }

    /// <summary>
    /// Total count of active operations.
    /// </summary>
    public required int TotalCount { get; init; }

    /// <summary>
    /// Operation type filter that was applied (if any).
    /// </summary>
    public OperationType? FilteredByType { get; init; }
}

/// <summary>
/// Response containing operations of a specific type.
/// </summary>
internal sealed record OperationsByTypeResponse
{
    /// <summary>
    /// The operation type.
    /// </summary>
    public required OperationType OperationType { get; init; }

    /// <summary>
    /// List of operations of this type, each serialized with its concrete progress type
    /// plus stable operationId/type fields for generic clients.
    /// </summary>
    public required JsonElement[] Operations { get; init; }

    /// <summary>
    /// Total count of operations.
    /// </summary>
    public required int TotalCount { get; init; }
}

/// <summary>
/// JSON serialization context for operations progress endpoints.
/// </summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.General)]
[System.Text.Json.Serialization.JsonSerializable(typeof(IOperationProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(UploadProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ImportProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(IngestProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(GeoservicesImportProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TileOperationProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ExportProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(PrintProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(RasterImportProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(RasterImportPhase))]
[System.Text.Json.Serialization.JsonSerializable(typeof(GeoprocessingProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(GeoprocessingWorkflowStatus))]
[System.Text.Json.Serialization.JsonSerializable(typeof(GeoprocessingStageKind))]
[System.Text.Json.Serialization.JsonSerializable(typeof(GeoprocessingStageStatus))]
[System.Text.Json.Serialization.JsonSerializable(typeof(PublishingProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(PublishIntentStatus))]
[System.Text.Json.Serialization.JsonSerializable(typeof(WorkflowProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(WorkflowRunStatus))]
[System.Text.Json.Serialization.JsonSerializable(typeof(DeploymentProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(DeploymentStatus))]
[System.Text.Json.Serialization.JsonSerializable(typeof(RolloutState))]
[System.Text.Json.Serialization.JsonSerializable(typeof(CancelOperationResponse))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ActiveOperationsResponse))]
[System.Text.Json.Serialization.JsonSerializable(typeof(OperationsByTypeResponse))]
[System.Text.Json.Serialization.JsonSerializable(typeof(OperationType))]
[System.Text.Json.Serialization.JsonSerializable(typeof(OperationStatus))]
internal sealed partial class OperationsProgressJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
