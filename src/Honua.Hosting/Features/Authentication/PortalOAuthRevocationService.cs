// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;

namespace Honua.Infrastructure.Authentication;

// An access token is revoked by evicting its cache entry and a refresh token by removing it, and holding the token is the authorization to revoke it.
internal sealed class PortalOAuthRevocationService(
    IPortalTokenIssuer tokenIssuer,
    PortalOAuthStore store,
    PortalJwtAccessTokenService jwtService)
{
    private readonly IPortalTokenIssuer _tokenIssuer = tokenIssuer;
    private readonly PortalOAuthStore _store = store;
    private readonly PortalJwtAccessTokenService _jwtService = jwtService;

    // The token type hint is advisory: the value is removed from both stores so a hint cannot leave the other form live, and an unknown value still succeeds.
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
