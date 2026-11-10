// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Orchestration.Abstractions;

namespace Honua.Geoprocessing;

internal sealed class GeoprocessingWorkflowJobExecutor : IWorkflowJobExecutor
{
    private readonly IGeoprocessingJobService _jobService;

    public GeoprocessingWorkflowJobExecutor(IGeoprocessingJobService jobService)
    {
        ArgumentNullException.ThrowIfNull(jobService);
        _jobService = jobService;
    }

    // The authorized plan is returned because a ForEach step's concrete layer id exists only after expansion at run creation, so publication-time pins cannot cover it.
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
