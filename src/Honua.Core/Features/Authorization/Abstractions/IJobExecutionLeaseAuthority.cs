// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>Issues and validates bounded job/intent-specific execution delegation independently of JWT renewal.</summary>
public interface IJobExecutionLeaseAuthority
{
    /// <summary>Issues only after fresh canonical executor, resource, side-effect and approval policy validation.</summary>
    /// <param name="request">Server-prepared immutable job and live executor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A server-sealed lease to persist atomically with that exact job.</returns>
    Task<JobExecutionLease> IssueAsync(JobExecutionLeaseIssuanceRequest request, CancellationToken cancellationToken = default);

    /// <summary>Revalidates against the authoritative persisted job, revocation and current executor lineage.</summary>
    /// <param name="job">Actual job being dispatched; its identity and intent must match the authoritative store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A matching job context carrying only transient bounded execution authority.</returns>
    Task<JobSecurityContext> RevalidateForExecutionAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default);

    /// <summary>Checks retained lease integrity and live lineage without granting execution authority on terminal jobs.</summary>
    /// <param name="job">Actual job identity and immutable accepted intent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RequireCurrentLineageAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default);

    /// <summary>Observes through current observer grants and executor lineage without substituting an executor principal.</summary>
    /// <param name="jobId">Actual persisted accepted job identity.</param>
    /// <param name="observer">Fresh authenticated human or permitted observer.</param>
    /// <param name="kind">Metadata or content authority being requested.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Non-capability observation metadata.</returns>
    Task<JobExecutionLeaseObservation> ResolveForObservationAsync(string jobId, ClaimsPrincipal observer,
        JobExecutionLeaseObservationKind kind, CancellationToken cancellationToken = default);
}

/// <summary>Canonical policy adapter for issuance and retained approval evidence.</summary>
public interface IJobExecutionLeasePolicy
{
    /// <summary>Evaluates actual operation and side-effect approval policy; no caller-provided approved boolean is accepted.</summary>
    /// <param name="request">Server-prepared job and actual live executor.</param>
    /// <param name="scope">Scope derived by the registered job-kind adapter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The auditable current policy decision.</returns>
    Task<JobExecutionLeasePolicyDecision> AuthorizeIssuanceAsync(JobExecutionLeaseIssuanceRequest request,
        JobExecutionLeaseScope scope, CancellationToken cancellationToken = default);

    /// <summary>Re-evaluates current policy for every sealed operation, including newly required approval, and validates retained evidence.</summary>
    /// <param name="job">Authoritative accepted job.</param>
    /// <param name="decision">Original sealed policy decision.</param>
    /// <param name="scope">Private copy of the original sealed exact canonical operation and resource closure.</param>
    /// <param name="currentPolicy">Closed current authorization and approval evaluation for exact sealed operations.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RequireCurrentAsync(ExecutionJobRecord job, JobExecutionLeasePolicyDecision decision, JobExecutionLeaseScope scope,
        IJobExecutionLeaseCurrentPolicy currentPolicy,
        CancellationToken cancellationToken = default);
}

/// <summary>Closed resource/provenance authority for one supported job kind; unknown adapters fail closed.</summary>
public interface IJobExecutionLeaseScopeProvider
{
    /// <summary>Exact job kind handled by this adapter.</summary>
    ExecutionJobKind Kind { get; }
    /// <summary>Derives the bounded resource and side-effect scope from accepted server-owned intent.</summary>
    /// <param name="job">Server-prepared immutable job.</param>
    /// <param name="executor">Actual fresh executor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The complete supported scope.</returns>
    Task<JobExecutionLeaseScope> PrepareAsync(ExecutionJobRecord job, ClaimsPrincipal executor,
        CancellationToken cancellationToken = default);
    /// <summary>Validates current canonical resources and original row/field policy without widening the sealed scope.</summary>
    /// <param name="job">Authoritative job.</param>
    /// <param name="scope">Original sealed scope.</param>
    /// <param name="context">Original trusted policy context with fresh transient execution authority.</param>
    /// <param name="principal">Principal whose exact resource rights are being checked.</param>
    /// <param name="kind">Execution uses null; observations specify their required read surface.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RequireCurrentAsync(ExecutionJobRecord job, JobExecutionLeaseScope scope, JobSecurityContext context,
        ClaimsPrincipal principal, JobExecutionLeaseObservationKind? kind, CancellationToken cancellationToken = default);
}
