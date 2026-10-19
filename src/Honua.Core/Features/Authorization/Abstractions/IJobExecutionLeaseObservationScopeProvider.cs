// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>Closed read-only source validation; exposes no executor principal or execution capability.</summary>
public interface IJobExecutionLeaseProvenanceValidator
{
    /// <summary>Rechecks one exact sealed resource/action through current canonical source authority.</summary>
    /// <param name="resource">An exact member of the original sealed resource closure.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RequireResourceAsync(JobExecutionLeaseResource resource, CancellationToken cancellationToken = default);

    /// <summary>Computes the current bounded metadata/RLS/mask fingerprint using immutable original policy inputs.</summary>
    /// <param name="resource">An exact catalog resource in the original sealed closure.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A non-capability policy fingerprint; no claim values or executable SQL are returned.</returns>
    Task<string> GetPolicyFingerprintAsync(JobExecutionLeaseResource resource, CancellationToken cancellationToken = default);
}

/// <summary>Optional complete historical read adapter; missing adapters refuse expired-lease observations.</summary>
public interface IJobExecutionLeaseObservationScopeProvider
{
    /// <summary>Validates actual terminal job/output provenance and the independent current observer's read authority.</summary>
    /// <param name="job">Authoritative accepted terminal job, not a preparation or caller DTO.</param>
    /// <param name="scope">Original sealed resource/action closure.</param>
    /// <param name="observer">Actual current observer; never replaced by the executor.</param>
    /// <param name="kind">Status metadata or output content.</param>
    /// <param name="provenance">Closed origin validation API with no executable identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RequireObservationAsync(ExecutionJobRecord job, JobExecutionLeaseScope scope, ClaimsPrincipal observer,
        JobExecutionLeaseObservationKind kind, IJobExecutionLeaseProvenanceValidator provenance,
        CancellationToken cancellationToken = default);
}

/// <summary>Historical receipt integrity policy separate from permission to execute again.</summary>
public interface IJobExecutionLeaseObservationPolicy
{
    /// <summary>Checks retained canonical approval evidence for a terminal observation without renewing execution authority.</summary>
    /// <param name="job">Authoritative terminal job.</param>
    /// <param name="decision">Original protected decision and receipt references.</param>
    /// <param name="scope">Private copy of the original sealed canonical scope.</param>
    /// <param name="observer">Actual current observer.</param>
    /// <param name="kind">Requested read surface.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RequireObservationAsync(ExecutionJobRecord job, JobExecutionLeasePolicyDecision decision, JobExecutionLeaseScope scope,
        ClaimsPrincipal observer, JobExecutionLeaseObservationKind kind, CancellationToken cancellationToken = default);
}
