// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Db.Postgres.Features.Admin;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

internal sealed class LayerPublishJobService(
    ILayerPublishingService publishingService,
    IOptions<LayerPublishingOptions> options,
    LayerPublishJobDispatcher dispatcher,
    IUniversalProgressStore progressStore,
    ITenantContext tenantContext,
    ISchemaContext? schemaContext = null,
    IExecutionJobStore? jobStore = null) : ILayerPublishJobs
{
    public async Task<LayerPublishAdmission> SubmitAsync(
        string connectionString,
        Guid connectionId,
        LayerPublishRequest request,
        string routeId,
        CancellationToken requestCancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
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

        if (dispatcher.Mode == LayerPublishRunnerMode.Unavailable)
        {
            return new LayerPublishAdmission(LayerPublishAdmissionKind.Unavailable, null, null, rowCount, null);
        }

        var jobId = Guid.NewGuid().ToString("N");
        var payload = new LayerPublishJobPayload
        {
            ConnectionId = connectionId,
            ConnectionKey = routeId,
            RowCount = rowCount,
            Request = LayerPublishRequestMapper.ToHttp(request),
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
            TenantId = tenantContext.TenantId,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Queued",
        };

        await dispatcher.DispatchAsync(payload, queued, request.Table).ConfigureAwait(false);
        return Queued(jobId, rowCount, routeId);
    }

    public async Task<LayerPublishJobProgress?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        var progress = await progressStore.GetProgressAsync<LayerPublishJobProgress>(jobId, cancellationToken)
            .ConfigureAwait(false);
        if (progress is null || !string.Equals(progress.TenantId, tenantContext.TenantId, StringComparison.Ordinal))
        {
            return null;
        }

        if (IsTerminal(progress.Status) || jobStore is null)
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

        var completion = LayerPublishCompletionReceipt.Read(job);
        if (job.Status == ExecutionJobStatus.Succeeded && (progress.LayerId ?? completion?.LayerId) is not > 0)
        {
            return progress with
            {
                Status = OperationStatus.Failed,
                CompletedAt = job.CompletedAt ?? job.UpdatedAt,
                CurrentPhase = "Failed",
                ErrorMessage = "Layer publish finished without a recorded result.",
            };
        }

        return job.Status switch
        {
            ExecutionJobStatus.Succeeded => progress with
            {
                Status = OperationStatus.Completed,
                CompletedAt = job.CompletedAt ?? job.UpdatedAt,
                CurrentPhase = job.CurrentPhase ?? "Completed",
                LayerId = progress.LayerId ?? completion?.LayerId,
                RowsMaterialized = completion?.RowsMaterialized ?? progress.RowsMaterialized,
                RowsTotal = completion?.RowsTotal ?? progress.RowsTotal,
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

    private static bool IsTerminal(OperationStatus status)
        => status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled;
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
        services.AddScoped<LayerPublishJobDispatcher>();
        services.AddScoped<ILayerPublishJobs, LayerPublishJobService>();
        return services;
    }
}
