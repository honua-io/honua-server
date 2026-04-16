// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Orchestration.Abstractions;

/// <summary>
/// Shared abstraction the workflow orchestration engine uses to submit and observe
/// canonical analysis-plan jobs. Implementations adapt a concrete job substrate
/// (e.g., the geoprocessing job service) so orchestration stays decoupled from
/// any single feature slice.
/// </summary>
public interface IWorkflowJobExecutor
{
    /// <summary>
    /// Submits a plan for asynchronous execution.
    /// </summary>
    Task<ExecutionJobRecord> SubmitJobAsync(
        AnalysisPlan plan,
        string? idempotencyKey,
        ClaimsPrincipal principal,
        IReadOnlyDictionary<string, string>? protocolMetadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a job record by identifier.
    /// </summary>
    Task<ExecutionJobRecord> GetJobAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the result package for a completed job.
    /// </summary>
    Task<AnalysisResultPackage> GetJobResultsAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);
}
