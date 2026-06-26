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
