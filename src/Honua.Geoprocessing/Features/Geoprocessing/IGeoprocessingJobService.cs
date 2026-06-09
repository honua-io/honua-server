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
