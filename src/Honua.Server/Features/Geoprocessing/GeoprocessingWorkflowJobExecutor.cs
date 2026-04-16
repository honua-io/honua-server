// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Orchestration.Abstractions;

namespace Honua.Server.Features.Geoprocessing;

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
}
