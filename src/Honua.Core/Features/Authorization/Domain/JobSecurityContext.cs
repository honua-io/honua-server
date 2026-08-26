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
/// The snapshot records exactly the identity the submitter presented. Deferred-submission lanes
/// replace its role claims from the configured live membership source when one can resolve the
/// principal. Deployments whose identity source cannot answer membership queries retain the
/// snapshot as the explicit fallback authority, so operators must republish workflows and
/// resubmit approvals after revoking roles in that mode (honua-server#3081).
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

/// <summary>
/// Well-known claim types carried inside a <see cref="JobSecurityContext"/> snapshot to drive
/// deferred-lane role revalidation (honua-server#3081).
/// </summary>
public static class JobSecurityContextClaimTypes
{
    /// <summary>Framework-derived authentication scheme retained for durable trust decisions.</summary>
    public const string AuthenticationScheme = "honua:auth_scheme";

    /// <summary>
    /// Framework-derived upstream issuer used only to re-query managed membership after an
    /// operator session has been exchanged for a server-issued bearer credential.
    /// </summary>
    public const string MembershipIssuer = "honua:membership_issuer";

    /// <summary>
    /// Framework-derived identifier used only to re-query the configured live membership source.
    /// This remains separate from <see cref="JobSecurityContext.PrincipalId"/>, which may carry an
    /// issuer-qualified canonical actor for durable attribution and cross-request identity checks.
    /// </summary>
    public const string MembershipPrincipalId = "honua:membership-principal-id";

    /// <summary>
    /// Marks a captured principal whose role membership is authoritatively owned by the
    /// configured live <c>IPrincipalMembershipSource</c> — a managed SCIM/OIDC-provisioned
    /// identity rather than a federated identity the source does not mirror.
    /// </summary>
    /// <remarks>
    /// The identity layer stamps this marker when it establishes the principal, so it travels
    /// with the durable snapshot independently of which replica later revalidates. That
    /// replica-independence is the whole point: a node-local membership store cannot tell
    /// "this principal is not managed" from "this principal is managed but was provisioned on
    /// another replica / under a different identifier", so on a resolution miss the snapshot
    /// marker is the only trustworthy signal. When present, a deferred lane that cannot
    /// re-resolve the principal MUST fail closed instead of trusting the captured role
    /// snapshot, because those roles could otherwise keep authorizing deferred work after the
    /// managed identity was deactivated or had roles revoked. The marker is produced upstream
    /// (the OIDC/SCIM claims surface, honua-server#3062); this contract is its consumer.
    /// </remarks>
    public const string ManagedMembershipMarker = "honua:managed-membership";

    /// <summary>Value stamped on <see cref="ManagedMembershipMarker"/> for a managed identity.</summary>
    public const string ManagedMembershipMarkerValue = "true";
}
