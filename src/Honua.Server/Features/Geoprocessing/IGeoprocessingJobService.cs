// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Shared domain-level service for geoprocessing job lifecycle.
/// Consumed by gRPC, REST (GPServer), and OGC API Processes adapters.
/// Works with domain types and <see cref="ClaimsPrincipal"/>; adapters
/// catch domain exceptions and translate to their protocol error format.
/// </summary>
internal interface IGeoprocessingJobService
{
    /// <summary>
    /// Validates a plan for executability and returns the validation result.
    /// </summary>
    PlanValidationResult ValidatePlan(AnalysisPlan plan, ClaimsPrincipal principal);

    /// <summary>
    /// Performs a dry run of a plan and returns cost/artifact estimates.
    /// </summary>
    DryRunResult DryRunPlan(AnalysisPlan plan, ClaimsPrincipal principal);

    /// <summary>
    /// Submits a plan for asynchronous execution and returns the job record.
    /// </summary>
    Task<ExecutionJobRecord> SubmitJobAsync(
        AnalysisPlan plan,
        string? idempotencyKey,
        ClaimsPrincipal principal,
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
