// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Single implementation of "which role names does this principal present?" for the
/// row/field security pipeline (honua-server#3068).
/// </summary>
/// <remarks>
/// The row-level-security and field-mask sources previously each carried a private copy of this
/// enumeration. The geoprocessing submit path now captures the same set into a durable
/// <see cref="JobSecurityContext"/> so a background read resolves the identical policy set, and
/// that only holds if capture and evaluation enumerate roles the same way — hence one shared
/// implementation rather than a third copy.
/// </remarks>
internal static class PrincipalRoleSnapshot
{
    /// <summary>
    /// Enumerates the principal's role values from the standard <see cref="ClaimTypes.Role"/>
    /// claim plus the deployment's configured role claim type, preserving order and allowing
    /// the caller to observe an empty list when the principal presents no roles.
    /// </summary>
    /// <param name="principal">The principal whose roles are enumerated.</param>
    /// <param name="options">RBAC options declaring the configured role claim type.</param>
    /// <returns>The principal's role values.</returns>
    public static List<string> Enumerate(ClaimsPrincipal principal, RbacOptions options)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(options);

        var roles = new List<string>();

        foreach (var claim in principal.FindAll(ClaimTypes.Role).Where(claim => !string.IsNullOrWhiteSpace(claim.Value)))
        {
            roles.Add(claim.Value);
        }

        var roleClaimType = options.EffectiveRoleClaimType;
        if (!string.Equals(roleClaimType, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var claim in principal.FindAll(roleClaimType).Where(claim => !string.IsNullOrWhiteSpace(claim.Value)))
            {
                roles.Add(claim.Value);
            }
        }

        return roles;
    }
}
