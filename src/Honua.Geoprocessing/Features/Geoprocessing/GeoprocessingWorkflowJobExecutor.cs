// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
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
    /// <paramref name="principal"/> and returns the plan with the gate's bindings stamped on it.
    /// </summary>
    /// <remarks>
    /// The bound plan used to be discarded here, on the reasoning that publication had already
    /// persisted the binding with the workflow definition. That holds only for a STATIC layer
    /// id: a ForEach step's concrete id exists only after expansion at run creation, so
    /// publication saw a placeholder and stamped nothing, and reconciliation then submitted an
    /// unpinned step that the layer gate refuses. Run creation is where the requester's
    /// authorization of the expanded step happens, so its result has to travel to dispatch
    /// (honua-server#3043 review).
    /// </remarks>
    public Task<AnalysisPlan> EnsurePlanExecutionAuthorizedAsync(
        AnalysisPlan plan,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
        => _jobService.EnsurePlanExecutionTierAuthorizedAsync(plan, principal, cancellationToken);

    public Task<ExecutionJobRecord> SubmitJobAsync(
        AnalysisPlan plan,
        string? idempotencyKey,
        ClaimsPrincipal principal,
        IReadOnlyDictionary<string, string>? protocolMetadata = null,
        JobSecurityContext? submitterSecurityContext = null,
        CancellationToken cancellationToken = default)
        => _jobService.SubmitJobWithSecurityContextAsync(
            plan, idempotencyKey, principal, protocolMetadata, submitterSecurityContext, cancellationToken);

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
