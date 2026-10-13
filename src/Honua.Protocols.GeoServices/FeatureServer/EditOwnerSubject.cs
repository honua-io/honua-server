// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Resolves the stable subject stamped and compared as a feature or replica owner.
/// </summary>
internal static class EditOwnerSubject
{
    /// <summary>
    /// Returns <see cref="ClaimTypes.NameIdentifier"/>, then <c>sub</c>, then <c>api_key_id</c>,
    /// and only then <see cref="ClaimsIdentity.Name"/> when none of those exist.
    /// </summary>
    /// <param name="principal">The caller, or <c>null</c>.</param>
    /// <returns>The owner subject, or <c>null</c> when none is present.</returns>
    public static string? Resolve(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return null;
        }

        var subject = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(subject))
        {
            return subject;
        }

        subject = principal.FindFirst("sub")?.Value;
        if (!string.IsNullOrWhiteSpace(subject))
        {
            return subject;
        }

        subject = principal.FindFirst("api_key_id")?.Value;
        if (!string.IsNullOrWhiteSpace(subject))
        {
            return subject;
        }

        subject = principal.Identity?.Name;
        return string.IsNullOrWhiteSpace(subject) ? null : subject;
    }
}
