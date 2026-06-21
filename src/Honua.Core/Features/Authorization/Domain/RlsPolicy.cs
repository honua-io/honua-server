// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// Row-level security (RLS) policy (#502). Attaches a row-visibility predicate to a
/// service/layer, keyed by the role it applies to. At query time the policy is
/// translated into a parameterized SQL predicate comparing a layer attribute against
/// the value(s) of a request claim, and AND-ed into the WHERE clause server-side so a
/// user only sees the rows their role permits.
/// </summary>
/// <remarks>
/// <para>The policy is intentionally <em>structured</em> (attribute + claim + operator)
/// rather than free-form SQL. The attribute name is validated against the resource
/// schema and the claim value is bound as a query parameter, so a policy can never
/// introduce SQL injection regardless of claim contents.</para>
/// <para>When a user's role has a matching policy but the user carries no value for the
/// referenced claim, the predicate evaluates to <c>FALSE</c> (no rows) — RLS is
/// fail-secure: absence of a claim never widens visibility.</para>
/// </remarks>
public sealed class RlsPolicy
{
    /// <summary>
    /// Unique identifier for this policy.
    /// </summary>
    public Guid PolicyId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Role name this policy applies to (case-insensitive), or "*" for every role.
    /// A request is constrained by the union of policies for all of its roles.
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// Service name this policy applies to, or "*" for all services.
    /// </summary>
    public required string Service { get; init; }

    /// <summary>
    /// Layer identifier this policy applies to, or "*" for all layers.
    /// </summary>
    public required string Layer { get; init; }

    /// <summary>
    /// The layer attribute (field name) the predicate filters on (e.g. "region").
    /// Validated against the resource schema before translation.
    /// </summary>
    public required string Attribute { get; init; }

    /// <summary>
    /// The claim type whose value(s) constrain the attribute (e.g. "region").
    /// All values the principal carries for this claim are matched.
    /// </summary>
    public required string ClaimType { get; init; }

    /// <summary>
    /// How the attribute is compared against the claim value(s).
    /// </summary>
    public RlsComparison Comparison { get; init; } = RlsComparison.In;

    /// <summary>
    /// Optional human-readable description of the policy's intent.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// When the policy was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the policy was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Comparison applied between an RLS attribute and the request claim value(s).
/// </summary>
public enum RlsComparison
{
    /// <summary>
    /// Row is visible when the attribute equals any of the principal's claim values
    /// (the principal may carry several values for the same claim). This is the
    /// default and the multi-tenant/region case from the ticket DoD.
    /// </summary>
    In = 0,

    /// <summary>
    /// Row is visible when the attribute equals the principal's single claim value.
    /// When the principal carries multiple values for the claim, the first is used.
    /// </summary>
    Equals = 1,
}
