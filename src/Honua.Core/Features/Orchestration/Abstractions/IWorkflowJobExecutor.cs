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
    /// Evaluates the execution-tier authorization required to run <paramref name="plan"/>
    /// against <paramref name="principal"/>, throwing when the principal lacks the grant a
    /// mutating (or otherwise elevated) step requires. Workflow run creation calls this
    /// against the REQUESTING principal <em>before</em> any step job is submitted under the
    /// synthesized orchestrator identity, so an <c>Execute</c>-only operator cannot schedule
    /// a workflow whose compiled steps import, mutate, or sink under an admin-bypassing
    /// system principal that never faces the mutating-process tier (#2798).
    /// </summary>
    Task EnsurePlanExecutionAuthorizedAsync(
        AnalysisPlan plan,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Cancels an in-flight job owned by this substrate. Implementations should swallow
    /// "already terminal" and "not found" conditions so orchestration cascade cancel
    /// semantics stay idempotent. Throws only for transport-level failures the caller
    /// can surface as warnings.
    /// </summary>
    Task CancelJobAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);
}
