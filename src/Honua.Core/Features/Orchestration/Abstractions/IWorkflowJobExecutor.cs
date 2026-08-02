// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
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
    /// Evaluates the plan-scoped authorization required to run <paramref name="plan"/>
    /// against <paramref name="principal"/>, throwing when the principal lacks the grant a
    /// mutating (or otherwise elevated) step requires, or read access to a catalog layer a
    /// layer-sourced step would read. Workflow run creation calls this against the REQUESTING
    /// principal <em>before</em> any step job is submitted under the synthesized orchestrator
    /// identity, so an <c>Execute</c>-only operator cannot schedule a workflow whose compiled
    /// steps import, mutate, or sink under an admin-bypassing system principal that never
    /// faces the mutating-process tier (#2798), nor one whose steps read a layer the operator
    /// cannot read on any protocol surface (#2283/#3043/#3046).
    /// </summary>
    /// <returns>
    /// The plan with the gate's per-layer bindings stamped on it. Callers MUST persist this
    /// instance for dispatch rather than the plan they passed in: for a ForEach step the
    /// concrete layer/dataset id exists only after expansion, so publication could not stamp a
    /// pin and the stored definition carries none. Discarding the bound plan therefore left
    /// reconciliation submitting an unpinned step, which the layer gate refuses
    /// (honua-server#3043 review).
    /// </returns>
    Task<AnalysisPlan> EnsurePlanExecutionAuthorizedAsync(
        AnalysisPlan plan,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a plan for asynchronous execution.
    /// </summary>
    /// <param name="plan">The plan to submit.</param>
    /// <param name="idempotencyKey">Optional idempotency key.</param>
    /// <param name="principal">The principal the submission's authorization gates evaluate against.</param>
    /// <param name="protocolMetadata">Protocol metadata for the submission.</param>
    /// <param name="submitterSecurityContext">
    /// The row/field security identity to pin on the job (honua-server#3068). The orchestration
    /// engine passes the RUN REQUESTER's snapshot here, because <paramref name="principal"/> is
    /// the synthesized orchestrator identity carrying <c>role=admin</c> and capturing from it
    /// would give a step job broader row/field visibility than the operator who scheduled the
    /// run. <see langword="null"/> means the run record predates the snapshot field;
    /// implementations MUST refuse the submission rather than fall back to capturing from
    /// <paramref name="principal"/>, which would launder admin visibility onto every step of a
    /// pre-upgrade run. Such runs must be resubmitted.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ExecutionJobRecord> SubmitJobAsync(
        AnalysisPlan plan,
        string? idempotencyKey,
        ClaimsPrincipal principal,
        IReadOnlyDictionary<string, string>? protocolMetadata = null,
        JobSecurityContext? submitterSecurityContext = null,
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
