// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.ControlPlane;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.Admin;

/// <summary>Launches accepted publications and compensates failed durable submissions.</summary>
internal sealed partial class LayerPublishJobDispatcher(
    LayerPublishRunnerDecision decision,
    IUniversalProgressStore progressStore,
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    ILogger<LayerPublishJobDispatcher> logger,
    IExecutionJobStore? jobStore = null,
    IJobQueue? jobQueue = null,
    IEnumerable<IBatchComputeBackend>? backends = null)
{
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(30);
    private const string WorkloadPrefix = "layer-publish:";

    internal LayerPublishRunnerMode Mode => decision.Mode;

    internal async Task DispatchAsync(LayerPublishJobPayload payload, LayerPublishJobProgress queued, string table)
    {
        var jobId = queued.OperationId;
        var rowCount = payload.RowCount;
        if (decision.Mode == LayerPublishRunnerMode.InProcess)
        {
            await progressStore.SetProgressAsync(jobId, queued, LayerPublishJobWork.ProgressRetention, CancellationToken.None)
                .ConfigureAwait(false);
            var stopping = lifetime.ApplicationStopping;
            _ = Task.Run(
                () => LayerPublishJobWork.RunInProcessAsync(
                    scopeFactory, progressStore, logger, payload, jobId, stopping),
                CancellationToken.None);
            return;
        }

        if (jobStore is null || (decision.Mode == LayerPublishRunnerMode.DurableLocal && jobQueue is null))
        {
            throw new InvalidOperationException("The durable layer-publish runner is not composed.");
        }

        var job = BuildJobRecord(jobId, payload, table, DateTimeOffset.UtcNow);
        using var dispatch = new CancellationTokenSource(DispatchTimeout);
        if (!await jobStore.TryCreateAsync(job, cancellationToken: dispatch.Token).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Failed to create layer-publish execution job '{jobId}'.");
        }

        try
        {
            job = await jobStore.GetAsync(jobId, dispatch.Token).ConfigureAwait(false) ?? job;
            await progressStore.SetProgressAsync(jobId, queued, LayerPublishJobWork.ProgressRetention, dispatch.Token)
                .ConfigureAwait(false);
            if (decision.Mode == LayerPublishRunnerMode.DurableLocal)
            {
                await jobQueue!.EnqueueAsync(jobId, cancellationToken: dispatch.Token).ConfigureAwait(false);
            }
            else
            {
                var backend = (backends ?? []).Resolve(job.Spec.Backend, job.Spec.TargetKind)
                    ?? throw new InvalidOperationException("The selected remote layer-publish backend is not composed.");
                await ExecutionJobSubmissionHelper.StartOnRemoteBackendAsync(
                    job, backend, jobStore, progressStore, LayerPublishJobWork.ProgressRetention, logger, dispatch.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ExecutionJobSubmissionHelper.TryRollbackCreatedJobAsync(
                jobStore,
                jobId,
                failureMessage: "Layer-publish submission failed.",
                logger: logger,
                cancellationToken: cleanup.Token).ConfigureAwait(false);
            await TryWriteFailedAsync(jobId, queued).ConfigureAwait(false);
            Log.DispatchFailed(logger, jobId, exception);
            throw;
        }

        Log.JobSubmitted(logger, jobId, table, rowCount);
    }

    private ExecutionJobRecord BuildJobRecord(
        string jobId,
        LayerPublishJobPayload payload,
        string table,
        DateTimeOffset now)
    {
        var workload = decision.Workload;
        var parameters = workload is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(workload.Parameters, StringComparer.Ordinal);
        parameters[LayerPublishJobPayload.ParameterName] = payload.ToJson();
        parameters["rowsTotal"] = payload.RowCount.ToString(CultureInfo.InvariantCulture);
        return new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            Audit = ControlPlaneTelemetry.WithSubmissionTraceContext(new OperationAuditInfo(), Activity.Current),
            TimeoutPolicy = JobTimeoutPolicy.LongRunning,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.LayerPublish,
                TargetKind = workload?.TargetKind ?? BatchComputeTargetKind.KubernetesJob,
                Backend = workload?.Backend ?? LocalBatchComputeBackend.BackendId,
                WorkloadId = workload?.WorkloadId,
                WorkloadName = workload?.WorkloadName ?? WorkloadPrefix + table,
                Artifact = workload?.ArtifactReference,
                RuntimeProfile = workload?.RuntimeProfile,
                ContractVersion = workload?.ContractVersion ?? 1,
                Parameters = parameters,
            },
        };
    }

    private async Task TryWriteFailedAsync(string jobId, LayerPublishJobProgress queued)
    {
        try
        {
            await progressStore.SetProgressAsync(jobId, queued with
            {
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = LayerPublishJobWork.SafeFailureMessage,
                CurrentPhase = "Failed",
            }, LayerPublishJobWork.ProgressRetention, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.DispatchFailed(logger, jobId, exception);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 8215, Level = LogLevel.Information, Message = "Queued layer publish {JobId} for {Table} ({RowCount} rows)")]
        public static partial void JobSubmitted(ILogger logger, string jobId, string table, long rowCount);

        [LoggerMessage(EventId = 8216, Level = LogLevel.Warning, Message = "Layer publish {JobId} was not queued")]
        public static partial void DispatchFailed(ILogger logger, string jobId, Exception exception);
    }
}
