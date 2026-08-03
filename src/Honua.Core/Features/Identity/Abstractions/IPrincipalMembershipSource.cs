// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Identity.Abstractions;

/// <summary>
/// Resolves a principal's current role membership from the configured identity source.
/// </summary>
/// <remarks>
/// Implementations return <see langword="null"/> when the source does not manage the
/// principal or cannot answer membership queries for that identity-provider mode. Exceptions
/// represent a source failure and must not be translated into snapshot fallback by callers.
/// </remarks>
public interface IPrincipalMembershipSource
{
    /// <summary>
    /// Resolves current account state and roles for a stable principal identifier.
    /// </summary>
    /// <param name="principalId">Stable principal identifier captured at submission time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Current membership, or <see langword="null"/> when this source cannot authoritatively
    /// resolve the principal.
    /// </returns>
    Task<PrincipalMembership?> ResolveMembershipAsync(
        string principalId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Current account state and role membership returned by an identity source.
/// </summary>
/// <param name="IsActive">Whether the identity remains active.</param>
/// <param name="Roles">Current role names. Inactive identities should return an empty set.</param>
public sealed record PrincipalMembership(bool IsActive, IReadOnlyList<string> Roles);
