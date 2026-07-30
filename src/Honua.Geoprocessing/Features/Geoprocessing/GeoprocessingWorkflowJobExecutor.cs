// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Orchestration.Abstractions;

namespace Honua.Geoprocessing;

/// <summary>
/// Adapts the geoprocessing job service to the canonical
/// <see cref="IWorkflowJobExecutor"/> substrate consumed by the workflow
/// orchestration engine. Keeps the orchestration feature decoupled from
/// geoprocessing internals while reusing canonical job semantics.
/// </summary>
internal sealed class GeoprocessingWorkflowJobExecutor : IWorkflowJobExecutor
{
    private readonly IGeoprocessingJobService _jobService;

    public GeoprocessingWorkflowJobExecutor(IGeoprocessingJobService jobService)
    {
        ArgumentNullException.ThrowIfNull(jobService);
        _jobService = jobService;
    }

    /// <summary>
    /// Evaluates the plan's execution-tier and per-layer authorization for
    /// <paramref name="principal"/>. The bound plan the job service returns is deliberately
    /// not surfaced on the orchestration substrate: run creation is a check, and the
    /// requester's dataset-layer binding is persisted upstream with the workflow definition
    /// (<c>WorkflowPackageService.PublishVersionAsync</c>) so it reaches the reconcile tick's
    /// dispatch through the stored plan rather than through run state (#3043 review).
    /// </summary>
    public Task EnsurePlanExecutionAuthorizedAsync(
        AnalysisPlan plan,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
        => _jobService.EnsurePlanExecutionTierAuthorizedAsync(plan, principal, cancellationToken);

    public Task<ExecutionJobRecord> SubmitJobAsync(
        AnalysisPlan plan,
        string? idempotencyKey,
        ClaimsPrincipal principal,
        IReadOnlyDictionary<string, string>? protocolMetadata = null,
        CancellationToken cancellationToken = default)
        => _jobService.SubmitJobAsync(plan, idempotencyKey, principal, protocolMetadata, cancellationToken);

    public Task<ExecutionJobRecord> GetJobAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
        => _jobService.GetJobAsync(jobId, principal, cancellationToken);

    public Task<AnalysisResultPackage> GetJobResultsAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
        => _jobService.GetJobResultsAsync(jobId, principal, cancellationToken);

    public async Task CancelJobAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _jobService.CancelJobAsync(jobId, principal, cancellationToken).ConfigureAwait(false);
        }
        catch (GeoprocessingNotFoundException)
        {
            // The child job was already pruned; orchestration cascade cancel is still successful.
        }
        catch (GeoprocessingPreconditionFailedException)
        {
            // The child job already reached a terminal state on its own; no further action required.
        }
    }
}
