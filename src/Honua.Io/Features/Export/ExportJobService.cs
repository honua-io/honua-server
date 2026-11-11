// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Progress;

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
    internal const string RequestParameter = "honua.export.request";
    internal static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    // The job worker's local queue; the same backend id and target kind every serving-node job uses.
    private const string LocalBackend = "local";

    public async Task<string> StartAsync(ExportJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (jobStore is null || jobQueue is null)
        {
            throw new InvalidOperationException(DurableQueueUnavailableMessage);
        }

        var progress = ExportProgress.CreateInitial(job.JobId, job.Format, job.ServiceName, job.LayerId, job.TotalFeatures);
        await progressStore.SetProgressAsync(job.JobId, progress, Retention, cancellationToken).ConfigureAwait(false);

        bool created;
        try
        {
            created = await jobStore.TryCreateAsync(CreateRecord(job), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await progressStore.DeleteProgressAsync(job.JobId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (!created)
        {
            await progressStore.DeleteProgressAsync(job.JobId, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException($"Failed to create export execution job '{job.JobId}'.");
        }

        try
        {
            await jobQueue.EnqueueAsync(job.JobId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        // The record is durable, so the export is accepted: the reconciler's dispatch repair enqueues a
        // local job that was created but not queued.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExportJobServiceLog.EnqueueDeferredToDispatchRepair(logger, job.JobId, ex);
        }

        return job.JobId;
    }

    internal static bool TryReadRequest(ExecutionJobRecord record, [NotNullWhen(true)] out ExportJob? job)
    {
        job = null;
        if (!record.Spec.Parameters.TryGetValue(RequestParameter, out var json) || string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            job = JsonSerializer.Deserialize(json, ExportJsonContext.Default.ExportJob);
        }
        catch (JsonException)
        {
            return false;
        }

        return job is not null && string.Equals(job.JobId, record.OperationId, StringComparison.Ordinal);
    }

    private static ExecutionJobRecord CreateRecord(ExportJob job)
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
                WorkloadName = $"export:{job.Format}:{job.ServiceName}:{job.LayerId}",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [RequestParameter] = JsonSerializer.Serialize(job with { Submitter = null }, ExportJsonContext.Default.ExportJob)
                }
            }
        };
    }
}
