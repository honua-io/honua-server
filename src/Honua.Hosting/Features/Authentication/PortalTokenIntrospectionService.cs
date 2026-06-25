// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// RFC 7662 token introspection for the OAuth2 surface (ADR-0054, #1890). Answers
/// for both token formats Honua issues: the opaque, cache-backed portal token (the
/// default) and the optional signed JWT. Both ultimately resolve to a cache entry,
/// so a revoked (evicted) or expired token always introspects as inactive — the
/// single-source-of-truth revocation model (ADR-0049/0053) is preserved.
/// </summary>
internal sealed class PortalTokenIntrospectionService(
    IPortalTokenIssuer tokenIssuer,
    PortalJwtAccessTokenService jwtService,
    IOptions<PortalTokenAuthenticationOptions> tokenOptions)
{
    private readonly IPortalTokenIssuer _tokenIssuer = tokenIssuer;
    private readonly PortalJwtAccessTokenService _jwtService = jwtService;
    private readonly PortalTokenAuthenticationOptions _tokenOptions = tokenOptions.Value;

    /// <summary>Whether the introspection endpoint is enabled.</summary>
    public bool IntrospectionEnabled => _tokenOptions.OAuth2.Jwt.EnableIntrospection;

    /// <summary>
    /// Introspects a presented access token. Returns the active-token metadata, or
    /// <see langword="null"/> when the token is unknown, expired, revoked, or (for a
    /// JWT) fails signature/issuer/audience/lifetime validation. RFC 7662 §2.2
    /// requires that an inactive token leaks nothing beyond <c>active=false</c>, so
    /// every failure mode collapses to a single null result here.
    /// </summary>
    public async Task<PortalTokenIntrospection?> IntrospectAsync(
        string token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        // A JWT (three dot-separated base64url segments) is verified offline first;
        // the embedded jti is the cache reference we then confirm is still live. A
        // forged/expired/wrong-audience JWT yields no reference and introspects as
        // inactive without ever touching the cache.
        if (LooksLikeJwt(token))
        {
            var reference = await _jwtService.TryReadReferenceAsync(token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(reference))
            {
                return null;
            }

            return await _tokenIssuer.IntrospectAsync(reference, cancellationToken).ConfigureAwait(false);
        }

        // Otherwise treat it as the opaque format: the token value is itself the
        // cache reference.
        return await _tokenIssuer.IntrospectAsync(token, cancellationToken).ConfigureAwait(false);
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
