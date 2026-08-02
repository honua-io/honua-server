// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// One claim carried by a <see cref="JobSecurityContext"/>.
/// </summary>
/// <param name="Type">The claim type (e.g. a role claim type, or an RLS policy's claim type).</param>
/// <param name="Value">The claim value.</param>
public sealed record JobSecurityClaim(string Type, string Value);

/// <summary>
/// Durable snapshot of the SUBMITTING principal's row/field security identity, captured at
/// submit time and pinned on the job record so a background worker (which has no ambient
/// <c>HttpContext</c>) can resolve the same row-level-security predicate and field mask the
/// synchronous protocol surfaces would resolve for that caller (honua-server#3068).
/// </summary>
/// <remarks>
/// <para>
/// The snapshot carries the submitter's <em>identity inputs</em> — the claims their policies are
/// keyed on — and NOT a materialized SQL predicate or a resolved mask list. Serializing a raw
/// SQL fragment into a durable job record (replicated to Redis, and for out-of-process backends
/// onto a worker payload) would put an executable predicate on the wire and freeze policy at
/// submit time. Persisting the claim snapshot instead keeps the durable record free of
/// executable SQL and re-derives both the predicate and the mask from the CURRENT policy store
/// on every read, so tightening a policy takes effect on already-queued jobs.
/// </para>
/// <para>
/// Claims are needed rather than role names alone because an <c>RlsPolicy</c> declares the claim
/// type its predicate compares against (<c>attribute IN (claim values)</c>), which is frequently
/// not a role claim. Role claims are captured first and never truncated, so field masking — which
/// keys purely on roles — is always exact; if the (deliberately generous) claim budget is
/// exhausted, the dropped non-role claims can only ever make an RLS predicate MORE restrictive,
/// because a policy whose claim has no value translates to <c>FALSE</c> (deny all rows).
/// </para>
/// <para>
/// The snapshot can only attenuate: it is exactly the identity the submitter presented, so a job
/// can never resolve broader row/field visibility than its submitter had.
/// </para>
/// </remarks>
/// <param name="PrincipalId">Stable identifier of the submitting principal, when known.</param>
/// <param name="TenantId">Tenant the submitter belonged to, or <see langword="null"/> for the tenant-less default.</param>
/// <param name="Claims">The submitter's captured claims, role claims first.</param>
/// <param name="RoleClaimType">
/// Claim type the submitting identity used for <c>IsInRole</c>; absent on older snapshots,
/// which restore with the standard role claim type.
/// </param>
public sealed record JobSecurityContext(
    string? PrincipalId,
    string? TenantId,
    IReadOnlyList<JobSecurityClaim> Claims,
    string? RoleClaimType = null);
