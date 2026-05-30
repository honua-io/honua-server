// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// Baseline batch-compute adapter that maps canonical execution-job semantics onto
/// the in-process worker substrate. Bridges progress published by existing workers
/// (geoprocessing, import) back through the canonical reconciler without introducing
/// a second dispatch path.
/// </summary>
internal sealed class LocalBatchComputeBackend(
    IUniversalProgressStore progressStore,
    IJobCancellationNotifier cancellationNotifier) : IBatchComputeBackend
{
    internal const string BackendId = "local";

    public string BackendName => BackendId;

    public BatchComputeTargetKind TargetKind => BatchComputeTargetKind.KubernetesJob;

    public Task<BatchComputeBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true,
            SupportsLogStreaming = false,
            SupportsProgressPolling = true,
            SupportsRetry = false,
            SupportsArtifactStaging = false
        });

    public Task<BatchComputeSubmissionResult> StartAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new BatchComputeSubmissionResult
        {
            Status = ExecutionJobStatus.Running,
            ProviderOperationId = job.OperationId,
            Message = "Local in-process execution via baseline workers"
        });

    public async Task<BatchComputeObservation> ObserveAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
    {
        var progress = await progressStore.GetProgressAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
        return BuildObservation(job, progress);
    }

    public async Task<BatchComputeObservation> CancelAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
    {
        cancellationNotifier.Cancel(job.OperationId);

        if (job.Status == ExecutionJobStatus.Queued)
        {
            return new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                ProviderOperationId = job.OperationId,
                PercentComplete = job.PercentComplete,
                Message = "Cancelled before worker pickup"
            };
        }

        return await ObserveAsync(job, cancellationToken).ConfigureAwait(false);
    }

    private static BatchComputeObservation BuildObservation(ExecutionJobRecord job, IOperationProgress? progress)
    {
        if (progress == null)
        {
            return new BatchComputeObservation
            {
                Status = job.Status,
                ProviderOperationId = job.OperationId,
                PercentComplete = job.PercentComplete,
                Message = job.CurrentPhase
            };
        }

        var mappedStatus = MapStatus(progress.Status, job.Status);
        return new BatchComputeObservation
        {
            Status = mappedStatus,
            ProviderOperationId = job.OperationId,
            PercentComplete = progress.PercentComplete ?? job.PercentComplete,
            Message = progress.CurrentPhase ?? progress.ErrorMessage ?? job.CurrentPhase
        };
    }

    private static ExecutionJobStatus MapStatus(OperationStatus progressStatus, ExecutionJobStatus currentStatus)
        => progressStatus switch
        {
            OperationStatus.Queued => currentStatus == ExecutionJobStatus.Queued
                ? ExecutionJobStatus.Queued
                : currentStatus,
            OperationStatus.Processing => ExecutionJobStatus.Running,
            OperationStatus.Completed => ExecutionJobStatus.Succeeded,
            OperationStatus.Failed => ExecutionJobStatus.Failed,
            OperationStatus.Cancelled => ExecutionJobStatus.Cancelled,
            _ => currentStatus
        };
}
