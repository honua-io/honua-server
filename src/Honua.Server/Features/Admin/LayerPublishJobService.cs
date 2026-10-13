// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.ControlPlane;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Db.Postgres.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

internal enum LayerPublishAdmissionKind
{
    Completed,
    Queued,
    Unavailable,
}

internal sealed record LayerPublishAdmission(
    LayerPublishAdmissionKind Kind,
    PublishedLayerSummary? Layer,
    string? JobId,
    long RowsTotal,
    string? StatusUrl);

/// <summary>
/// Accepts a layer publish. At or under the row threshold the publish runs to completion on the
/// publication budget, not on <see cref="CancellationToken"/> from the request. Above the threshold
/// it becomes a pollable job on the shared execution-job queue, or an in-process task when that
/// queue is absent. A frozen host is refused.
/// </summary>
internal interface ILayerPublishJobs
{
    Task<LayerPublishAdmission> SubmitAsync(
        string connectionString,
        Guid connectionId,
        LayerPublishRequest request,
        PublishLayerRequest httpRequest,
        string routeId,
        CancellationToken requestCancellation);

    Task<LayerPublishJobProgress?> GetAsync(string jobId, CancellationToken cancellationToken);
}

internal sealed partial class LayerPublishJobService(
    ILayerPublishingService publishingService,
    IOptions<LayerPublishingOptions> options,
    LayerPublishRunnerDecision decision,
    IUniversalProgressStore progressStore,
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    ITenantContext tenantContext,
    ILogger<LayerPublishJobService> logger,
    ISchemaContext? schemaContext = null,
    IExecutionJobStore? jobStore = null,
    IJobQueue? jobQueue = null) : ILayerPublishJobs
{
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(30);
    private const string WorkloadPrefix = "layer-publish:";

    public async Task<LayerPublishAdmission> SubmitAsync(
        string connectionString,
        Guid connectionId,
        LayerPublishRequest request,
        PublishLayerRequest httpRequest,
        string routeId,
        CancellationToken requestCancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(httpRequest);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);

        // Pre-accept only. An aborted count publishes nothing and rolls nothing back.
        var rowCount = await publishingService.CountSourceRowsAsync(
            connectionString, request.Schema, request.Table, requestCancellation).ConfigureAwait(false);
        var threshold = options.Value.GetValidatedAsyncPublishRowThreshold();
        if (rowCount <= threshold)
        {
            var summary = await publishingService.PublishLayerAsync(
                connectionString, request, CancellationToken.None).ConfigureAwait(false);
            return new LayerPublishAdmission(LayerPublishAdmissionKind.Completed, summary, null, rowCount, null);
        }

        if (decision.Mode == LayerPublishRunnerMode.Unavailable)
        {
            return new LayerPublishAdmission(LayerPublishAdmissionKind.Unavailable, null, null, rowCount, null);
        }

        var jobId = Guid.NewGuid().ToString("N");
        var payload = new LayerPublishJobPayload
        {
            ConnectionId = connectionId,
            ConnectionKey = routeId,
            RowCount = rowCount,
            Request = httpRequest,
            TenantId = tenantContext.TenantId,
            TenantSource = tenantContext.Source,
            CurrentSchema = schemaContext?.CurrentSchema,
        };
        var queued = new LayerPublishJobProgress
        {
            OperationId = jobId,
            Status = OperationStatus.Queued,
            RowsMaterialized = 0,
            RowsTotal = rowCount,
            ConnectionId = connectionId,
            ConnectionKey = routeId,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Queued",
        };

        if (decision.Mode == LayerPublishRunnerMode.InProcess)
        {
            await progressStore.SetProgressAsync(jobId, queued, LayerPublishJobWork.ProgressRetention, CancellationToken.None)
                .ConfigureAwait(false);
            var stopping = lifetime.ApplicationStopping;
            _ = Task.Run(
                () => LayerPublishJobWork.RunInProcessAsync(
                    scopeFactory, progressStore, logger, payload, jobId, stopping),
                CancellationToken.None);
            return Queued(jobId, rowCount, routeId);
        }

        if (jobStore is null || jobQueue is null)
        {
            throw new InvalidOperationException("The durable layer-publish runner is not composed.");
        }

        var job = BuildJobRecord(jobId, payload, request.Table, DateTimeOffset.UtcNow);
        using var dispatch = new CancellationTokenSource(DispatchTimeout);
        if (!await jobStore.TryCreateAsync(job, cancellationToken: dispatch.Token).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Failed to create layer-publish execution job '{jobId}'.");
        }

        try
        {
            await progressStore.SetProgressAsync(jobId, queued, LayerPublishJobWork.ProgressRetention, dispatch.Token)
                .ConfigureAwait(false);
            await jobQueue.EnqueueAsync(jobId, cancellationToken: dispatch.Token).ConfigureAwait(false);
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

        Log.JobSubmitted(logger, jobId, request.Table, rowCount);
        return Queued(jobId, rowCount, routeId);
    }

    public async Task<LayerPublishJobProgress?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        var progress = await progressStore.GetProgressAsync<LayerPublishJobProgress>(jobId, cancellationToken)
            .ConfigureAwait(false);
        if (progress is null || IsTerminal(progress.Status) || jobStore is null)
        {
            return progress;
        }

        var job = await jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        return job is null ? progress : ProjectExecutionOutcome(progress, job);
    }

    internal static string StatusUrl(string routeId, string jobId)
        => $"/api/v1/admin/connections/{Uri.EscapeDataString(routeId)}/layers/jobs/{jobId}";

    internal static LayerPublishJobProgress ProjectExecutionOutcome(LayerPublishJobProgress progress, ExecutionJobRecord job)
    {
        if (job.Spec.Kind != ExecutionJobKind.LayerPublish || IsTerminal(progress.Status))
        {
            return progress;
        }

        return job.Status switch
        {
            ExecutionJobStatus.Succeeded => progress with
            {
                Status = OperationStatus.Completed,
                CompletedAt = job.CompletedAt ?? job.UpdatedAt,
                CurrentPhase = job.CurrentPhase ?? "Completed",
                LayerId = progress.LayerId ?? TryReadLayerId(job),
            },
            ExecutionJobStatus.Failed => progress with
            {
                Status = OperationStatus.Failed,
                CompletedAt = job.CompletedAt ?? job.UpdatedAt,
                ErrorMessage = progress.ErrorMessage ?? LayerPublishJobWork.SafeFailureMessage,
                CurrentPhase = job.CurrentPhase ?? "Failed",
            },
            ExecutionJobStatus.Cancelled => progress with
            {
                Status = OperationStatus.Cancelled,
                CompletedAt = job.CompletedAt ?? job.UpdatedAt,
                CurrentPhase = job.CurrentPhase ?? "Cancelled",
            },
            _ => progress,
        };
    }

    private static LayerPublishAdmission Queued(string jobId, long rowCount, string routeId)
        => new(LayerPublishAdmissionKind.Queued, null, jobId, rowCount, StatusUrl(routeId, jobId));

    private static ExecutionJobRecord BuildJobRecord(
        string jobId,
        LayerPublishJobPayload payload,
        string table,
        DateTimeOffset now)
        => new()
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
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBatchComputeBackend.BackendId,
                WorkloadName = WorkloadPrefix + table,
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [LayerPublishJobPayload.ParameterName] = payload.ToJson(),
                    ["rowsTotal"] = payload.RowCount.ToString(CultureInfo.InvariantCulture),
                },
            },
        };

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

    private static int? TryReadLayerId(ExecutionJobRecord job)
    {
        const string prefix = "layer-publish:";
        foreach (var reference in job.CommittedEffectReferences.Concat(job.ArtifactReferences))
        {
            if (reference.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(reference[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
            {
                return layerId;
            }
        }

        return null;
    }

    private static bool IsTerminal(OperationStatus status)
        => status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled;

    private static partial class Log
    {
        [LoggerMessage(EventId = 8215, Level = LogLevel.Information, Message = "Queued layer publish {JobId} for {Table} ({RowCount} rows)")]
        public static partial void JobSubmitted(ILogger logger, string jobId, string table, long rowCount);

        [LoggerMessage(EventId = 8216, Level = LogLevel.Warning, Message = "Layer publish {JobId} was not queued")]
        public static partial void DispatchFailed(ILogger logger, string jobId, Exception exception);
    }
}

internal static class LayerPublishServiceCollectionExtensions
{
    internal static IServiceCollection AddLayerPublishJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(static descriptor => descriptor.ServiceType == typeof(LayerPublishRunnerDecision)))
        {
            return services;
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobExecutor, LayerPublishJobExecutor>());
        services.AddSingleton(static provider => LayerPublishRunnerDecision.Resolve(provider));
        services.AddScoped<ILayerPublishJobs, LayerPublishJobService>();
        return services;
    }
}
