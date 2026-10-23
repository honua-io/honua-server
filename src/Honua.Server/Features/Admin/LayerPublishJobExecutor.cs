// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Worker for <see cref="ExecutionJobKind.LayerPublish"/> on the shared job loop (honua-server#5948).
/// </summary>
internal sealed class LayerPublishJobExecutor(
    IServiceScopeFactory scopeFactory,
    IUniversalProgressStore progressStore,
    ILogger<LayerPublishJobExecutor> logger) : IJobExecutor
{
    public ExecutionJobKind Kind => ExecutionJobKind.LayerPublish;

    public Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);
        if (!LayerPublishJobPayload.TryParse(job.Spec.Parameters, out var payload, out var error) || payload is null)
        {
            return Task.FromResult(JobExecutionResult.Failed($"Invalid layer-publish job spec: {error}") with
            {
                IsRetryable = false
            });
        }

        return LayerPublishJobWork.ExecuteAsync(
            scopeFactory, progressStore, logger, payload, job.OperationId, context, cancellationToken);
    }
}
