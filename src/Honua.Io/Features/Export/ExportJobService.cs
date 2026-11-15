// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Progress;
using Honua.Migration;

namespace Honua.Io.Export;

internal interface IExportJobService
{
    Task<string> StartAsync(ExportJob job, CancellationToken cancellationToken = default);
}

// An accepted export is an Export execution job: the record and its queue position are rows in the
// system database, so it runs once on whichever node claims it and survives the node that accepted it.
internal sealed class ExportJobService(
    IUniversalProgressStore progressStore,
    ILogger<ExportJobService> logger,
    IExecutionJobStore? jobStore = null,
    IJobQueue? jobQueue = null) : IExportJobService
{
    internal const string DurableQueueUnavailableMessage = "Large exports require durable background job storage to be configured.";
    internal const string QueueFullMessage = "Export queue is full. Please retry later.";

    // At most five exports may be queued or running across all nodes.
    internal const int MaxOutstandingExports = 5;
    internal const string RequestParameter = "honua.export.request";
    internal static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    // The job worker's local queue; the same backend id and target kind every serving-node job uses.
    private const string LocalBackend = "local";

    public async Task<string> StartAsync(ExportJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (jobStore is not IBoundedExecutionJobStore boundedStore || jobQueue is null)
        {
            throw new InvalidOperationException(DurableQueueUnavailableMessage);
        }

        await CreateJobAsync(boundedStore, job, cancellationToken).ConfigureAwait(false);
        await EnqueueAsync(jobQueue, job.JobId, cancellationToken).ConfigureAwait(false);
        return job.JobId;
    }

    // The queued progress is written first, so a job never exists without the progress its status URL reads.
    private async Task CreateJobAsync(IBoundedExecutionJobStore store, ExportJob job, CancellationToken cancellationToken)
    {
        var progress = ExportProgress.CreateInitial(job.JobId, job.Format, job.ServiceName, job.ServiceLayerIndex, job.TotalFeatures);
        await progressStore.SetProgressAsync(job.JobId, progress, Retention, cancellationToken).ConfigureAwait(false);

        bool created;
        try
        {
            created = await store.TryCreateWithinCapacityAsync(CreateRecord(job, progress), MaxOutstandingExports, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await progressStore.DeleteProgressAsync(job.JobId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (!created)
        {
            await progressStore.DeleteProgressAsync(job.JobId, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException(QueueFullMessage);
        }
    }

    // The record is durable, so the export is accepted: the next reconciliation sweep queues a local
    // job whose enqueue was lost.
    private async Task EnqueueAsync(IJobQueue queue, string jobId, CancellationToken cancellationToken)
    {
        try
        {
            await queue.EnqueueAsync(jobId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExportJobServiceLog.EnqueueDeferredToDispatchRepair(logger, jobId, ex);
        }
    }

    internal static ExportJob? ReadRequest(ExecutionJobRecord record)
    {
        if (!record.Spec.Parameters.TryGetValue(RequestParameter, out var json) || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var job = JsonSerializer.Deserialize(json, ExportJsonContext.Default.ExportJob);
            return job is not null && string.Equals(job.JobId, record.OperationId, StringComparison.Ordinal)
                ? job
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ExecutionJobRecord CreateRecord(ExportJob job, ExportProgress progress)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = job.JobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            // The worker runs the export under the submitter's identity; it travels on the record,
            // not in the request parameter.
            Audit = new OperationAuditInfo
            {
                RequestedBy = job.Submitter?.PrincipalId,
                SubmitterSecurityContext = job.Submitter
            },
            // A layer large enough to export asynchronously can outlast the one-hour default.
            TimeoutPolicy = JobTimeoutPolicy.LongRunning,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Export,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBackend,
                WorkloadName = $"export:{job.Format}:{job.ServiceName}:{job.ServiceLayerIndex}",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [RequestParameter] = JsonSerializer.Serialize(job with { Submitter = null }, ExportJsonContext.Default.ExportJob),
                    [ExecutionJobProgressReader.InitialProgressParameter] = OperationProgressSerializer.Serialize(progress)
                }
            }
        };
    }
}
