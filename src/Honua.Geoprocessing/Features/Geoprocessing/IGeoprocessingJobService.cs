// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Shared domain-level service for geoprocessing job lifecycle.
/// Consumed by gRPC, REST (GPServer), and OGC API Processes adapters.
/// Works with domain types and <see cref="ClaimsPrincipal"/>; adapters
/// catch domain exceptions and translate to their protocol error format.
/// </summary>
internal interface IGeoprocessingJobService
{
    /// <summary>
    /// Validates that the caller has the required authorization for the specified operation.
    /// Enables adapters to enforce auth before protocol-specific validation.
    /// </summary>
    Task EnsureCallerAuthorizedAsync(
        ClaimsPrincipal principal,
        OperatorResourceType resourceType,
        OperatorOperation operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates the additional execution-tier authorization a plan requires against
    /// <paramref name="principal"/>, throwing <see cref="GeoprocessingAuthorizationException"/>
    /// when the principal lacks it. Two tiers are covered: the mutating-process tier
    /// (<see cref="OperatorOperation.ExecuteMutatingProcess"/>, #2798) and per-layer READ access
    /// on every catalog layer the plan's layer-sourced steps would read (#2283/#3043). Both are
    /// the same gates <see cref="SubmitJobAsync"/> applies, exposed so the workflow
    /// orchestration engine can evaluate the REQUESTING principal at run creation before step
    /// jobs are dispatched under the admin-bypassing orchestrator identity. Baseline
    /// <see cref="OperatorOperation.Execute"/> is assumed pre-checked by the caller.
    ///
    /// <para>
    /// Returns the plan with the layers this principal was authorized to read BOUND to each
    /// gated step. Authoring surfaces that persist a plan for later background dispatch —
    /// workflow publication above all — must store the returned plan rather than the input:
    /// the reconcile tick submits under an <c>admin</c>-carrying orchestrator identity that
    /// would otherwise re-derive the binding against whatever the dataset points at then, so
    /// the requester's binding has to travel with the durable plan and be matched at
    /// dispatch (#3043 review). Callers that only need the yes/no decision may discard it.
    /// </para>
    /// </summary>
    Task<AnalysisPlan> EnsurePlanExecutionTierAuthorizedAsync(
        AnalysisPlan plan,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a plan for executability and returns the validation result.
    /// Callers must pre-authorize via <see cref="EnsureCallerAuthorizedAsync"/> to
    /// guarantee auth-before-validation ordering at the adapter boundary.
    /// </summary>
    PlanValidationResult ValidatePlan(AnalysisPlan plan, ClaimsPrincipal principal);

    /// <summary>
    /// Performs a dry run of a plan and returns cost/artifact estimates.
    /// Callers must pre-authorize via <see cref="EnsureCallerAuthorizedAsync"/> to
    /// guarantee auth-before-validation ordering at the adapter boundary.
    /// </summary>
    DryRunResult DryRunPlan(AnalysisPlan plan, ClaimsPrincipal principal);

    /// <summary>
    /// Submits a plan for asynchronous execution and returns the job record.
    /// Callers must pre-authorize via <see cref="EnsureCallerAuthorizedAsync"/> to
    /// guarantee auth-before-validation ordering at the adapter boundary.
    /// </summary>
    /// <param name="plan">The analysis plan to execute.</param>
    /// <param name="idempotencyKey">Optional idempotency key for deduplication.</param>
    /// <param name="principal">The requesting principal.</param>
    /// <param name="protocolMetadata">Optional protocol-specific metadata stored in <see cref="ExecutionJobSpec.Parameters"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ExecutionJobRecord> SubmitJobAsync(
        AnalysisPlan plan,
        string? idempotencyKey,
        ClaimsPrincipal principal,
        IReadOnlyDictionary<string, string>? protocolMetadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes an approved destructive/sink plan whose submission was previously
    /// gated and persisted as an <c>AwaitingApproval</c> operation proposal
    /// (ADR-0064, #2814). Called by the operation gateway's approval path
    /// (<c>ApplyApprovedProposalAsync</c>) via <see cref="GeoprocessOperationExecutor"/>.
    /// The approval and mutating-process authorization gates were already satisfied
    /// when the proposal was created, so this re-submits the plan through the normal
    /// job pipeline with those gates bypassed, attributing the job to the original
    /// submitter recorded in the payload.
    /// </summary>
    /// <param name="payload">The persisted plan submission to replay.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// A default implementation throws <see cref="NotSupportedException"/> so the many
    /// hand-rolled adapter test doubles that implement this interface do not each need
    /// to stub the approval-resume path they never exercise; the production
    /// <see cref="GeoprocessingJobService"/> provides the real implementation.
    /// </remarks>
    Task<ExecutionJobRecord> ResumeApprovedJobAsync(
        GeoprocessExecutionPayload payload,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "This geoprocessing job service does not support resuming approved plans.");

    /// <summary>
    /// Retrieves a job record by identifier.
    /// </summary>
    Task<ExecutionJobRecord> GetJobAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the caller-visible geoprocessing jobs matching the supplied filter,
    /// newest first, with cursor paging. Applies the same per-job ownership check as
    /// <see cref="GetJobAsync"/>; jobs the caller cannot read are omitted from the
    /// page. Adapters supply protocol binding constraints (e.g. GPServer
    /// service/task) through <see cref="GeoprocessingJobListFilter.RequiredParameters"/>.
    /// </summary>
    Task<GeoprocessingJobListPage> ListJobsAsync(
        GeoprocessingJobListFilter filter,
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
    /// Cancels an in-flight job.
    /// </summary>
    Task CancelJobAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Filter and paging criteria for <see cref="IGeoprocessingJobService.ListJobsAsync"/>.
/// </summary>
internal sealed record GeoprocessingJobListFilter
{
    /// <summary>
    /// Spec parameters that a job must carry (case-insensitive value match) to be
    /// included. Used by protocol adapters to constrain the listing to jobs they own
    /// (e.g. GPServer service/task binding metadata).
    /// </summary>
    public IReadOnlyDictionary<string, string> RequiredParameters { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Optional status filter. When empty, jobs of any status (including terminal
    /// history) are returned.
    /// </summary>
    public IReadOnlyList<ExecutionJobStatus> Statuses { get; init; } = Array.Empty<ExecutionJobStatus>();

    /// <summary>Opaque cursor returned by a previous page.</summary>
    public string? Cursor { get; init; }

    /// <summary>Requested page size; clamped to the service's supported range.</summary>
    public int Limit { get; init; } = 50;
}

/// <summary>
/// A cursor page of geoprocessing job records visible to the caller.
/// </summary>
internal sealed record GeoprocessingJobListPage
{
    /// <summary>Jobs in this page, ordered newest first.</summary>
    public required IReadOnlyList<ExecutionJobRecord> Items { get; init; }

    /// <summary>Opaque cursor for the next page, or null when no more items remain.</summary>
    public string? NextCursor { get; init; }
}
