// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

internal interface IGeoprocessingJobService
{
    Task EnsureGPServerExecutionAuthorizedAsync(
        ClaimsPrincipal principal, GPServerExecutionContext execution, CancellationToken cancellationToken = default)
        => EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Process, OperatorOperation.Execute, cancellationToken);

    Task<ExecutionJobRecord> SubmitGPServerJobAsync(
        AnalysisPlan plan, string? idempotencyKey, ClaimsPrincipal principal,
        GPServerExecutionContext execution, IReadOnlyDictionary<string, string>? protocolMetadata = null,
        CancellationToken cancellationToken = default)
        => SubmitJobAsync(plan, idempotencyKey, principal, protocolMetadata, cancellationToken);

    Task<ExecutionJobRecord> GetGPServerJobAsync(
        string jobId, string serviceId, string? taskName, ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
        => GetJobAsync(jobId, principal, cancellationToken);

    Task<AnalysisResultPackage> GetGPServerJobResultsAsync(
        string jobId, string serviceId, string taskName, ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
        => GetJobResultsAsync(jobId, principal, cancellationToken);

    Task CancelGPServerJobAsync(
        string jobId, string serviceId, string? taskName, ClaimsPrincipal principal,
        bool isOrphanCleanup = false, CancellationToken cancellationToken = default)
        => isOrphanCleanup
            ? CancelAbandonedJobAsync(jobId, principal, cancellationToken)
            : CancelJobAsync(jobId, principal, cancellationToken);

    Task EnsureCallerAuthorizedAsync(
        ClaimsPrincipal principal,
        OperatorResourceType resourceType,
        OperatorOperation operation,
        CancellationToken cancellationToken = default);

    Task EnsureCallerAuthorizedAsync(
        ClaimsPrincipal principal,
        OperatorAuthorizationRequest request,
        CancellationToken cancellationToken = default)
        => EnsureCallerAuthorizedAsync(
            principal,
            request.ResourceType,
            request.Operation,
            cancellationToken);

    // The default throws instead of forwarding, because forwarding would pin the orchestrator admin identity and drop the supplied snapshot.
    Task<ExecutionJobRecord> SubmitJobWithSecurityContextAsync(
        AnalysisPlan plan,
        string? idempotencyKey,
        ClaimsPrincipal principal,
        IReadOnlyDictionary<string, string>? protocolMetadata,
        JobSecurityContext? submitterSecurityContext,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "This IGeoprocessingJobService implementation does not support submitting with an explicit submitter security context.");

    // Callers that persist a plan for later dispatch must store the returned plan, or reconciliation re-derives the pins under the orchestrator identity.
    Task<AnalysisPlan> EnsurePlanExecutionTierAuthorizedAsync(
        AnalysisPlan plan,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    PlanValidationResult ValidatePlan(AnalysisPlan plan, ClaimsPrincipal principal);

    DryRunResult DryRunPlan(AnalysisPlan plan, ClaimsPrincipal principal);

    Task<ExecutionJobRecord> SubmitJobAsync(
        AnalysisPlan plan,
        string? idempotencyKey,
        ClaimsPrincipal principal,
        IReadOnlyDictionary<string, string>? protocolMetadata = null,
        CancellationToken cancellationToken = default);

    Task<ExecutionJobRecord> SubmitProtocolJobAsync(
        AnalysisPlan plan,
        string? idempotencyKey,
        ClaimsPrincipal principal,
        IProcessCatalog protocolCatalog,
        IReadOnlyDictionary<string, string>? protocolMetadata = null,
        CancellationToken cancellationToken = default)
        => SubmitJobAsync(
            plan,
            idempotencyKey,
            principal,
            protocolMetadata,
            cancellationToken);

    Task<ExecutionJobRecord> ResumeApprovedJobAsync(
        GeoprocessExecutionPayload payload,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "This geoprocessing job service does not support resuming approved plans.");

    Task<ExecutionJobRecord> GetJobAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    // Ownership is enforced here without a separate job-read grant, and protocol adapters must not expose this seam.
    Task<ExecutionJobRecord> GetJobForTerminalAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
        => GetJobAsync(jobId, principal, cancellationToken);

    Task<GeoprocessingJobListPage> ListJobsAsync(
        GeoprocessingJobListFilter filter,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    Task<AnalysisResultPackage> GetJobResultsAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    Task<AnalysisResultPackage> GetJobResultsForTerminalAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
        => GetJobResultsAsync(jobId, principal, cancellationToken);

    Task CancelJobAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    Task CancelAbandonedJobAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
        => CancelJobAsync(jobId, principal, cancellationToken);
}

internal sealed record GeoprocessingJobListFilter
{
    public IReadOnlyDictionary<string, string> RequiredParameters { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<ExecutionJobStatus> Statuses { get; init; } = Array.Empty<ExecutionJobStatus>();

    public string? Cursor { get; init; }

    public int Limit { get; init; } = 50;
}

internal sealed record GeoprocessingJobListPage
{
    public required IReadOnlyList<ExecutionJobRecord> Items { get; init; }

    public string? NextCursor { get; init; }
}
