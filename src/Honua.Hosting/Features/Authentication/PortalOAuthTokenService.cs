// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Authorization.Abstractions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Handles <c>POST /sharing/rest/oauth2/token</c> grants for the ArcGIS OAuth2
/// named-user bridge (#1242): validates the Honua-issued authorization code (with
/// PKCE) or a refresh token, then mints the Esri-shaped access token through the
/// shared <see cref="IPortalTokenIssuer"/> (ADR-0049). The bridge never holds a
/// second token store — the access token is the same opaque, cache-backed portal
/// token that <c>generateToken</c> issues, so it validates on the request path via
/// the existing <c>PortalTokenAuthenticationHandler</c>.
/// </summary>
internal sealed class PortalOAuthTokenService(
    IPortalTokenIssuer tokenIssuer,
    PortalOAuthStore store,
    IOptions<PortalTokenAuthenticationOptions> tokenOptions)
{
    private const string AuthorizationCodeGrant = "authorization_code";
    private const string RefreshTokenGrant = "refresh_token";

    // ArcGIS OAuth2 refresh tokens are long lived; clamp to the portal max-token
    // lifetime so a refresh token never outlives the operator's configured ceiling
    // by an unbounded amount. 90 days mirrors the ArcGIS Online default.
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(90);

    private readonly IPortalTokenIssuer _tokenIssuer = tokenIssuer;
    private readonly PortalOAuthStore _store = store;
    private readonly PortalTokenAuthenticationOptions _tokenOptions = tokenOptions.Value;

    /// <summary>Exchanges a grant for an Esri-shaped token envelope.</summary>
    public async Task<PortalOAuthTokenResult> ExchangeAsync(
        PortalOAuthTokenRequest request,
        string requestBinding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.GrantType switch
        {
            AuthorizationCodeGrant => await ExchangeAuthorizationCodeAsync(request, requestBinding, cancellationToken).ConfigureAwait(false),
            RefreshTokenGrant => await ExchangeRefreshTokenAsync(request, requestBinding, cancellationToken).ConfigureAwait(false),
            _ => PortalOAuthTokenResult.Failure("unsupported_grant_type", "Only authorization_code and refresh_token grants are supported."),
        };
    }

    private async Task<PortalOAuthTokenResult> ExchangeAuthorizationCodeAsync(
        PortalOAuthTokenRequest request,
        string requestBinding,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return PortalOAuthTokenResult.Failure("invalid_request", "Authorization code is required.");
        }

        var record = await _store.ConsumeAuthorizationCodeAsync(request.Code, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return PortalOAuthTokenResult.Failure("invalid_grant", "Authorization code is invalid or expired.");
        }

        if (!string.IsNullOrWhiteSpace(request.ClientId) &&
            !string.Equals(record.ClientId, request.ClientId, StringComparison.Ordinal))
        {
            return PortalOAuthTokenResult.Failure("invalid_grant", "Authorization code was issued to a different client.");
        }

        if (!string.IsNullOrWhiteSpace(request.RedirectUri) &&
            !string.Equals(record.RedirectUri, request.RedirectUri, StringComparison.Ordinal))
        {
            return PortalOAuthTokenResult.Failure("invalid_grant", "redirect_uri does not match the authorization request.");
        }

        // Hard PKCE requirement (#1484): when enabled, an authorization code that was
        // issued without a registered code_challenge is rejected outright. Combined
        // with the authorize-endpoint guard this guarantees every code flow has had
        // PKCE — closing the authorization-code interception window for native
        // clients (ArcGIS Pro / Field Maps) that lack a confidential client secret.
        if (_tokenOptions.OAuth2.RequirePkce && string.IsNullOrWhiteSpace(record.CodeChallenge))
        {
            return PortalOAuthTokenResult.Failure("invalid_grant", "PKCE is required: no code_challenge was registered for this authorization code.");
        }

        if (!VerifyPkce(record.CodeChallenge, record.CodeChallengeMethod, request.CodeVerifier))
        {
            return PortalOAuthTokenResult.Failure("invalid_grant", "PKCE verification failed.");
        }

        return await IssueAsync(
            record.ClientId,
            record.Principal,
            record.ExpirationMinutes,
            requestBinding,
            request.IncludeRefreshToken,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PortalOAuthTokenResult> ExchangeRefreshTokenAsync(
        PortalOAuthTokenRequest request,
        string requestBinding,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return PortalOAuthTokenResult.Failure("invalid_request", "refresh_token is required.");
        }

        var record = await _store.GetRefreshTokenAsync(request.RefreshToken, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return PortalOAuthTokenResult.Failure("invalid_grant", "Refresh token is invalid or expired.");
        }

        if (!string.IsNullOrWhiteSpace(request.ClientId) &&
            !string.Equals(record.ClientId, request.ClientId, StringComparison.Ordinal))
        {
            return PortalOAuthTokenResult.Failure("invalid_grant", "Refresh token was issued to a different client.");
        }

        // Refresh-token rotation (#1484): when enabled (the default) the presented
        // token is revoked and a fresh refresh token is minted and returned, bounding
        // the replay window of a leaked token to a single use. The old token is
        // removed before issuing so a concurrent reuse races to a revoked entry. When
        // rotation is disabled the existing token is reused (legacy ArcGIS behavior).
        var rotate = _tokenOptions.OAuth2.RotateRefreshTokens;
        if (rotate)
        {
            await _store.RemoveRefreshTokenAsync(request.RefreshToken, cancellationToken).ConfigureAwait(false);
        }

        return await IssueAsync(
            record.ClientId,
            record.Principal,
            requestedMinutes: null,
            requestBinding,
            includeRefreshToken: rotate,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PortalOAuthTokenResult> IssueAsync(
        string clientId,
        PortalOAuthPrincipal principal,
        int? requestedMinutes,
        string requestBinding,
        bool includeRefreshToken,
        CancellationToken cancellationToken)
    {
        var ttlMinutes = ResolveExpirationMinutes(requestedMinutes);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(ttlMinutes);

        var issuance = await _tokenIssuer.IssueAsync(
            new PortalTokenIssueRequest(
                PrincipalId: principal.PrincipalId,
                DisplayName: principal.DisplayName,
                TenantId: principal.TenantId,
                Roles: principal.Roles,
                // OAuth2 bridge tokens are bound to the requesting client's referer
                // (the redirect host) so they behave like a generateToken referer
                // token rather than an IP-bound one.
                ClientType: PortalTokenClientType.Referer,
                BindingValue: requestBinding,
                ExpiresAt: expiresAt),
            cancellationToken).ConfigureAwait(false);

        string? refreshToken = null;
        if (includeRefreshToken)
        {
            refreshToken = await _store.CreateRefreshTokenAsync(
                new PortalOAuthRefreshToken
                {
                    ClientId = clientId,
                    Principal = principal,
                    ExpiresAt = DateTimeOffset.UtcNow.Add(RefreshTokenLifetime),
                },
                cancellationToken).ConfigureAwait(false);
        }

        // Esri oauth2/token reports expires_in in SECONDS (generateToken reports
        // `expires` in milliseconds); we honor the per-endpoint convention.
        var expiresInSeconds = (long)Math.Max(0, (issuance.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);

        return PortalOAuthTokenResult.Success(issuance.Token, expiresInSeconds, refreshToken);
    }

    private int ResolveExpirationMinutes(int? requested)
    {
        var defaultMinutes = _tokenOptions.DefaultExpirationMinutes > 0
            ? _tokenOptions.DefaultExpirationMinutes
            : PortalTokenAuthenticationOptions.DefaultExpirationMinutesValue;
        var maxMinutes = _tokenOptions.MaxExpirationMinutes > 0
            ? _tokenOptions.MaxExpirationMinutes
            : PortalTokenAuthenticationOptions.DefaultMaxExpirationMinutesValue;

        return requested is null
            ? Math.Min(defaultMinutes, maxMinutes)
            : Math.Min(requested.Value, maxMinutes);
    }

    private static bool VerifyPkce(string? challenge, string? method, string? verifier)
    {
        if (string.IsNullOrWhiteSpace(challenge))
        {
            // No challenge was registered at authorize time. This branch is only
            // reachable when OAuth2.RequirePkce is disabled (the default-on hard
            // requirement, #1484, rejects no-challenge codes before this call); in
            // that explicit opt-out there is nothing to verify.
            return true;
        }

        if (string.IsNullOrWhiteSpace(verifier))
        {
            return false;
        }

        if (string.Equals(method, "plain", StringComparison.OrdinalIgnoreCase))
        {
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(challenge),
                Encoding.UTF8.GetBytes(verifier));
        }

        // Default to S256 per RFC 7636.
        var computed = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(challenge),
            Encoding.UTF8.GetBytes(computed));
    }
}

/// <summary>Inputs parsed from <c>POST /sharing/rest/oauth2/token</c>.</summary>
internal sealed record PortalOAuthTokenRequest(
    string GrantType,
    string? Code,
    string? CodeVerifier,
    string? RedirectUri,
    string? ClientId,
    string? RefreshToken,
    bool IncludeRefreshToken);

/// <summary>Result of a token exchange, ready to serialize as the Esri envelope.</summary>
internal sealed record PortalOAuthTokenResult(
    bool Succeeded,
    string? AccessToken,
    long ExpiresInSeconds,
    string? RefreshToken,
    string? Error,
    string? ErrorDescription)
{
    /// <summary>Builds a successful token result.</summary>
    public static PortalOAuthTokenResult Success(string accessToken, long expiresInSeconds, string? refreshToken)
        => new(true, accessToken, expiresInSeconds, refreshToken, null, null);

    /// <summary>Builds an OAuth2 error result.</summary>
    public static PortalOAuthTokenResult Failure(string error, string description)
        => new(false, null, 0, null, error, description);
}
