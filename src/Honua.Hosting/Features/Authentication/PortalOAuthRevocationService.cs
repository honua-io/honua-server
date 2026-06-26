// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// RFC 7009 per-token revocation for the ArcGIS OAuth2 named-user bridge (#2155).
/// Revokes a single presented access or refresh token immediately so it fails every
/// subsequent authorization check across all replicas. Revocation is the dual of
/// <see cref="PortalTokenIntrospectionService"/>: both resolve a presented credential
/// to the single cache-backed source of truth (ADR-0049/0053). An access token is
/// revoked by evicting its backing portal-token cache entry (the JWT <c>jti</c> or
/// the opaque value itself); a refresh token is revoked by removing it from the
/// <see cref="PortalOAuthStore"/>. The presented token value is the authorization to
/// revoke it (the caller already holds the bearer credential), matching RFC 7009 and
/// ArcGIS sign-out semantics.
/// </summary>
internal sealed class PortalOAuthRevocationService(
    IPortalTokenIssuer tokenIssuer,
    PortalOAuthStore store,
    PortalJwtAccessTokenService jwtService)
{
    private readonly IPortalTokenIssuer _tokenIssuer = tokenIssuer;
    private readonly PortalOAuthStore _store = store;
    private readonly PortalJwtAccessTokenService _jwtService = jwtService;

    /// <summary>
    /// Revokes a presented token. The <paramref name="tokenTypeHint"/> (RFC 7009
    /// §2.1) is advisory; because a leaked credential should never be left live, the
    /// presented value is removed from both the access-token and refresh-token stores
    /// regardless of the hint. All removals are idempotent no-ops when the value is
    /// unknown, so RFC 7009 §2.2 (a revocation request always succeeds) is preserved.
    /// </summary>
    /// <param name="token">The access or refresh token to revoke.</param>
    /// <param name="tokenTypeHint">Optional <c>token_type_hint</c> (advisory only).</param>
    /// <param name="cancellationToken">Token used to abort the revocation.</param>
    public async Task RevokeAsync(
        string token,
        string? tokenTypeHint,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        _ = tokenTypeHint;

        // Access-token leg: a JWT (header.payload.signature) is verified offline and
        // its embedded jti is the cache reference; the opaque format is its own
        // reference. A forged/expired/wrong-audience JWT yields no reference, so the
        // access-token removal is skipped — but the refresh-token removal below still
        // runs in case the value is an opaque refresh token.
        var accessReference = LooksLikeJwt(token)
            ? await _jwtService.TryReadReferenceAsync(token).ConfigureAwait(false)
            : token;

        if (!string.IsNullOrWhiteSpace(accessReference))
        {
            await _tokenIssuer.RevokeAsync(accessReference, cancellationToken).ConfigureAwait(false);
        }

        // Refresh-token leg: refresh tokens are opaque values keyed directly in the
        // OAuth store. Removing rotates the named user out of any further silent
        // re-issuance, closing the long-lived credential.
        await _store.RemoveRefreshTokenAsync(token, cancellationToken).ConfigureAwait(false);
    }

    private static bool LooksLikeJwt(string token)
    {
        // A compact JWS has exactly two '.' separators (header.payload.signature).
        var first = token.IndexOf('.', StringComparison.Ordinal);
        if (first <= 0)
        {
            return false;
        }

        var second = token.IndexOf('.', first + 1);
        if (second <= first + 1 || second >= token.Length - 1)
        {
            return false;
        }

        return token.IndexOf('.', second + 1) < 0;
    }
}
