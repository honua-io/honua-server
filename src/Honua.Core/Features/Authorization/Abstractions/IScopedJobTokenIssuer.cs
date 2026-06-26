// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// Issues and validates opaque, job-bound, attenuated callback tokens for
/// custom-code geoprocessing (the Phase 0 auth spine). A scoped job token lets
/// future user-supplied geoprocessing code call back into Honua through the SDK
/// with authority that is <em>strictly ≤ the submitter's</em> — never more.
/// </summary>
/// <remarks>
/// <para>
/// This is a <em>separate</em> issuer from <see cref="IPortalTokenIssuer"/>: it
/// mirrors that mint pattern (opaque entropy + distributed-cache-backed record,
/// re-hydrating a <see cref="ClaimsPrincipal"/>) but adds a frozen
/// <see cref="ScopedJobTokenRequest.ResourceScope"/> and a hard binding to a
/// single <see cref="ScopedJobTokenRequest.JobId"/>. The ArcGIS portal token is
/// never overloaded to carry this attenuation.
/// </para>
/// <para>
/// The hydrated principal carries <em>only</em> the frozen intersection of the
/// owner's grants and the resource scope, expressed with the same role/permission
/// claim grammar the shared RBAC pipeline already enforces. Authorization is
/// therefore never forked: the same service-scoped-role and per-operation
/// permission checks every other request flows through enforce the attenuation.
/// </para>
/// </remarks>
public interface IScopedJobTokenIssuer
{
    /// <summary>
    /// Mints an opaque scoped-job token. The effective authority of the token is
    /// computed and <em>frozen server-side at this call</em> as the intersection
    /// of the owner snapshot's roles/grants and the requested
    /// <see cref="ScopedJobTokenRequest.ResourceScope"/>; nothing presented later
    /// can widen it.
    /// </summary>
    /// <param name="request">The mint request describing the pinned owner snapshot, job, scope, and expiry.</param>
    /// <param name="cancellationToken">Token used to abort the issuance.</param>
    /// <returns>The issued opaque token and its absolute expiry.</returns>
    Task<ScopedJobTokenIssuance> IssueAsync(
        ScopedJobTokenRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Validates an opaque scoped-job token presented within a job context and, on
    /// success, returns the hydrated attenuated principal. The token validates
    /// <em>only</em> when <paramref name="jobId"/> exactly matches the job the
    /// token was bound to (invariant #4); a mismatch, an unknown/expired token, or
    /// a revoked record returns <see langword="null"/>.
    /// </summary>
    /// <param name="token">The opaque token value supplied by the SDK callback.</param>
    /// <param name="jobId">The active job context the token is presented within.</param>
    /// <param name="cancellationToken">Token used to abort the lookup.</param>
    /// <returns>The validated attenuated session, or <see langword="null"/> when unusable.</returns>
    Task<ScopedJobTokenValidation?> ValidateAsync(
        string token,
        string jobId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Revokes a scoped-job token by deleting its backing record. Called when a job
    /// reaches a terminal state so the callback credential cannot outlive the job
    /// (invariant #5). Idempotent: revoking an already-absent token is a no-op.
    /// </summary>
    /// <param name="token">The opaque token value to revoke.</param>
    /// <param name="cancellationToken">Token used to abort the removal.</param>
    Task RevokeAsync(string token, CancellationToken cancellationToken);
}

/// <summary>
/// Inputs required to mint a scoped-job token. Every authority-bearing field is
/// taken from the pinned owner snapshot — never from a value the future job
/// supplies.
/// </summary>
/// <param name="PrincipalId">Stable identifier of the submitting principal (surfaced on the hydrated principal).</param>
/// <param name="TenantId">
/// Tenant captured from the submitter's <c>tenant_id</c> claim at submit time, or
/// <see langword="null"/> for the tenant-less default. This is the only source of
/// the hydrated principal's tenant — it is never widened by a callback-supplied
/// value (invariant #2).
/// </param>
/// <param name="Roles">Snapshot of the submitter's roles, used to compute the owner's reachable scope.</param>
/// <param name="Grants">
/// Snapshot of the submitter's per-resource <c>read:</c>/<c>write:{service}[/{layer}]</c>
/// permission grants. The mint intersects <see cref="ResourceScope"/> against BOTH
/// <see cref="Roles"/> and these grants, so a submitter whose write authority comes
/// purely from a permission grant (not a role) is no longer under-scoped. Like
/// <see cref="Roles"/> this is an owner-snapshot value — never callback-supplied — so
/// the result stays strictly ⊆ the submitter (invariant #1). May be empty.
/// </param>
/// <param name="JobId">The job this token is bound to; the token is honored only inside that job's context (invariant #4).</param>
/// <param name="ResourceScope">The requested resource scope; the effective grant is the intersection of this and the owner's reachable roles and grants (invariant #1).</param>
/// <param name="ExpiresAt">Absolute expiry; set to the job timeout plus a small grace (invariant #5).</param>
public sealed record ScopedJobTokenRequest(
    string PrincipalId,
    string? TenantId,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Grants,
    string JobId,
    IReadOnlyList<JobResourceScopeEntry> ResourceScope,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Result of minting a scoped-job token.
/// </summary>
/// <param name="Token">The opaque token to hand to the job's callback context.</param>
/// <param name="ExpiresAt">Absolute expiry of the token.</param>
public sealed record ScopedJobTokenIssuance(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// A validated scoped-job session: the hydrated, attenuated principal and the
/// token expiry.
/// </summary>
/// <param name="Principal">
/// The hydrated <see cref="ClaimsPrincipal"/> carrying only the frozen
/// intersection grants and the pinned tenant. Its authority flows through the
/// existing shared RBAC pipeline unchanged (invariant #6).
/// </param>
/// <param name="JobId">The job the token is bound to (echoed for callers that audit the binding).</param>
/// <param name="ExpiresAt">Absolute expiry of the token.</param>
public sealed record ScopedJobTokenValidation(
    ClaimsPrincipal Principal,
    string JobId,
    DateTimeOffset ExpiresAt);
