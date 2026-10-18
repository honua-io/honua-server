// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>Canonical classification of per-issuance token transport metadata.</summary>
/// <remarks>
/// These fields change when a token refreshes and do not independently grant authority.
/// Excluding them for lineage comparison must never remove or replace original policy
/// inputs: an RLS policy may intentionally reference any of these original values.
/// </remarks>
public static class CredentialIssuanceClaimTypes
{
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "exp", "iat", "nbf", "jti", "auth_time", ClaimTypes.AuthenticationInstant,
        "at_hash", "c_hash", "nonce", "uti", "aio", "rh"
    };

    /// <summary>Returns whether a claim is canonical token issuance metadata, using exact claim-type matching.</summary>
    /// <param name="claimType">The claim type.</param>
    /// <returns>Whether this type is known per-issuance transport metadata.</returns>
    public static bool Contains(string claimType) => Types.Contains(claimType);
}
