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
    IAdminApiKeyStore apiKeyStore,
    IOAuthClientStore clientStore,
    IOAuthScopeCatalogue scopeCatalogue,
    PortalJwtAccessTokenService jwtService,
    ClientCredentialsFederationService federation,
    IOptions<PortalTokenAuthenticationOptions> tokenOptions)
{
    private const string AuthorizationCodeGrant = "authorization_code";
    private const string RefreshTokenGrant = "refresh_token";
    private const string ClientCredentialsGrant = "client_credentials";

    // ArcGIS OAuth2 refresh tokens are long lived; clamp to the portal max-token
    // lifetime so a refresh token never outlives the operator's configured ceiling
    // by an unbounded amount. 90 days mirrors the ArcGIS Online default.
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(90);

    private readonly IPortalTokenIssuer _tokenIssuer = tokenIssuer;
    private readonly PortalOAuthStore _store = store;
    private readonly IAdminApiKeyStore _apiKeyStore = apiKeyStore;
    private readonly IOAuthClientStore _clientStore = clientStore;
    private readonly IOAuthScopeCatalogue _scopeCatalogue = scopeCatalogue;
    private readonly PortalJwtAccessTokenService _jwtService = jwtService;
    private readonly ClientCredentialsFederationService _federation = federation;
    private readonly PortalTokenAuthenticationOptions _tokenOptions = tokenOptions.Value;

    // Optional JWT access-token format (ADR-0068, #1890): when enabled the issued
    // access_token is a signed JWT whose jti is the cache reference, so revocation
    // and the single request-path validator are preserved. Off by default — the
    // opaque path below is byte-for-byte unchanged.
    private async Task<PortalTokenIssuance> MintAccessTokenAsync(
        PortalTokenIssueRequest issueRequest,
        CancellationToken cancellationToken)
        => _jwtService.JwtEnabled
            ? await _jwtService.IssueAsync(issueRequest, cancellationToken).ConfigureAwait(false)
            : await _tokenIssuer.IssueAsync(issueRequest, cancellationToken).ConfigureAwait(false);

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
            // client_credentials is only honored when the operator has explicitly
            // opted in (ADR-0053, #1860). With the flag off the grant falls through to
            // the unsupported_grant_type default below — byte-for-byte the prior
            // behavior — so no existing deployment gains a credential path implicitly.
            ClientCredentialsGrant when _tokenOptions.OAuth2.EnableClientCredentials
                => await ExchangeClientCredentialsAsync(request, cancellationToken).ConfigureAwait(false),
            _ => PortalOAuthTokenResult.Failure(
                "unsupported_grant_type",
                "Only authorization_code and refresh_token grants are supported."),
        };
    }

    private async Task<PortalOAuthTokenResult> ExchangeClientCredentialsAsync(
        PortalOAuthTokenRequest request,
        CancellationToken cancellationToken)
    {
        var secret = request.ClientSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            // RFC 6749 §5.2: a missing/invalid client credential is invalid_client.
            return PortalOAuthTokenResult.Failure("invalid_client", "client_secret is required for the client_credentials grant.");
        }

        // Increment 3 (ADR-0053, #1889): optional pluggable IdP/OIDC federation. When
        // configured, the presented client_id/client_secret are delegated to the
        // operator's external token endpoint first. A successful federated exchange
        // means the IdP authenticated the machine identity; Honua then mints its own
        // token carrying the operator-configured roles (no second token store —
        // ADR-0049). A federation miss falls through to the in-tree resolution below,
        // so federation is purely additive. The credential is resolved before the
        // IP-binding check (below) so an unknown/bad credential always returns
        // invalid_client (RFC 6749 §5.2), never the binding-resolution failure mode.
        if (_federation.FederationEnabled)
        {
            var federatedRoles = await _federation
                .TryAuthenticateAsync(request.ClientId, secret, request.Scope, cancellationToken)
                .ConfigureAwait(false);
            if (federatedRoles is not null)
            {
                var federatedIp = RequireClientIp(request);
                if (federatedIp is null)
                {
                    return PortalOAuthTokenResult.Failure("invalid_request", "Client IP could not be determined for token binding.");
                }

                return await MintClientCredentialsTokenAsync(
                    principalId: string.IsNullOrWhiteSpace(request.ClientId) ? "federated-client" : request.ClientId!,
                    roles: federatedRoles,
                    clientIp: federatedIp,
                    grantedScope: request.Scope,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // Increment 2 (ADR-0053, #1888): prefer the first-class OAuth client registry.
        // A client presents a real client_id/client_secret pair; the secret is matched
        // against the per-application registry (SHA-256-hashed at rest, revocable,
        // expiry-aware). Roles come from the scope catalogue: the requested scope is
        // resolved to RBAC permissions, bounded by the client's allowed scopes. The
        // credential is validated before the IP-binding check so an unknown/bad
        // credential always returns invalid_client (RFC 6749 §5.2), never leaking the
        // binding-resolution failure mode.
        OAuthClientRecord? client = null;
        if (!string.IsNullOrWhiteSpace(request.ClientId))
        {
            client = await _clientStore
                .ValidateSecretAsync(request.ClientId, secret, cancellationToken)
                .ConfigureAwait(false);
        }

        // Increment 1 backward-compatible fallback (ADR-0053): when no first-class
        // client matches, the client_secret is validated against the existing Admin
        // API-key store — the same SHA-256-hashed, expiry-aware, revocable key that
        // X-API-Key automation already uses. client_id is a human-readable label; the
        // secret alone authenticates. This keeps every flag-gated Increment-1
        // deployment working byte-for-byte.
        AdminApiKeyValidationResult? validation = null;
        if (client is null)
        {
            validation = await _apiKeyStore.ValidateAsync(secret, cancellationToken).ConfigureAwait(false);
            if (validation is null)
            {
                // Do not distinguish unknown-client from bad-secret (RFC 6749 §5.2).
                return PortalOAuthTokenResult.Failure("invalid_client", "Client authentication failed.");
            }
        }

        // The credential is valid; now bind the token to the issuing client's IP.
        // Service clients have no browser referer, so an IP binding mirrors how API-key
        // automation reaches the request path and is validated by
        // PortalTokenAuthenticationHandler (binding.ClientIp). This is checked AFTER
        // credential validation so an unknown/bad secret returns invalid_client, never
        // leaking the binding-resolution failure mode (ADR-0053).
        var clientIp = RequireClientIp(request);
        if (clientIp is null)
        {
            return PortalOAuthTokenResult.Failure("invalid_request", "Client IP could not be determined for token binding.");
        }

        if (client is not null)
        {
            return await IssueClientCredentialsAsync(client, request.Scope, clientIp, cancellationToken)
                .ConfigureAwait(false);
        }

        // Identity is the API-key record: its name labels the principal and its
        // permissions become the principal's roles, so the existing RBAC resolver
        // (#1375) makes the per-operation decision exactly as for any other principal.
        var roles = ResolveRoles(validation!.Record.Permissions, request.Scope);

        return await MintClientCredentialsTokenAsync(
            principalId: validation.Record.Name,
            roles: roles,
            clientIp: clientIp,
            grantedScope: null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PortalOAuthTokenResult> IssueClientCredentialsAsync(
        OAuthClientRecord client,
        string? requestedScope,
        string clientIp,
        CancellationToken cancellationToken)
    {
        // A first-class client must be permitted to use the client_credentials grant.
        // When no grant types are registered the client is unrestricted (back-compat
        // for a registration that omitted them); otherwise the grant must be listed.
        if (client.AllowedGrantTypes.Count > 0 &&
            !client.AllowedGrantTypes.Contains(ClientCredentialsGrant, StringComparer.OrdinalIgnoreCase))
        {
            // RFC 6749 §5.2: the client is not authorized for this grant type.
            return PortalOAuthTokenResult.Failure(
                "unauthorized_client",
                "This client is not authorized to use the client_credentials grant.");
        }

        // Resolve the requested scope to RBAC permissions through the catalogue,
        // bounded by the client's allowed scopes. Unknown/disallowed scopes are
        // dropped — never escalated. The token carries exactly the granted scopes.
        var grant = await _scopeCatalogue
            .ResolveGrantAsync(requestedScope, client.AllowedScopes, cancellationToken)
            .ConfigureAwait(false);

        var grantedScope = grant.GrantedScopes.Count > 0
            ? string.Join(' ', grant.GrantedScopes)
            : null;

        return await MintClientCredentialsTokenAsync(
            principalId: client.ClientId,
            roles: grant.GrantedPermissions,
            clientIp: clientIp,
            grantedScope: grantedScope,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PortalOAuthTokenResult> MintClientCredentialsTokenAsync(
        string principalId,
        IReadOnlyList<string> roles,
        string clientIp,
        string? grantedScope,
        CancellationToken cancellationToken)
    {
        var ttlMinutes = ResolveExpirationMinutes(requested: null);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(ttlMinutes);

        var issuance = await MintAccessTokenAsync(
            new PortalTokenIssueRequest(
                PrincipalId: principalId,
                DisplayName: principalId,
                TenantId: null,
                Roles: roles,
                ClientType: PortalTokenClientType.Ip,
                BindingValue: clientIp,
                ExpiresAt: expiresAt),
            cancellationToken).ConfigureAwait(false);

        var expiresInSeconds = (long)Math.Max(0, (issuance.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);

        // No refresh token for client_credentials (RFC 6749 §4.4.3): the client simply
        // re-requests with its own secret, avoiding a long-lived secondary credential.
        return PortalOAuthTokenResult.Success(issuance.Token, expiresInSeconds, refreshToken: null, scope: grantedScope);
    }

    private static string? RequireClientIp(PortalOAuthTokenRequest request)
        => string.IsNullOrWhiteSpace(request.ClientIp) ? null : request.ClientIp;

    private static IReadOnlyList<string> ResolveRoles(IReadOnlyList<string> keyPermissions, string? requestedScope)
    {
        // scope may only NARROW to roles the key already holds — a requested scope the
        // key lacks is dropped, never escalated (ADR-0053). Absent any usable scope,
        // the principal gets exactly the key's permissions.
        if (string.IsNullOrWhiteSpace(requestedScope))
        {
            return keyPermissions;
        }

        var requested = requestedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var narrowed = requested
            .Where(scope => keyPermissions.Contains(scope, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        return narrowed.Length == 0 ? keyPermissions : narrowed;
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

        // RFC 6749 §4.1.3: client_id and redirect_uri bindings are mandatory when the
        // authorization code carries them; omitting them from the token request must not
        // bypass the checks (an attacker who exfiltrates a code would otherwise redeem it
        // without proving they are the legitimate client).
        if (!string.IsNullOrWhiteSpace(record.ClientId))
        {
            if (string.IsNullOrWhiteSpace(request.ClientId))
            {
                return PortalOAuthTokenResult.Failure("invalid_grant", "client_id is required for this authorization code.");
            }

            if (!string.Equals(record.ClientId, request.ClientId, StringComparison.Ordinal))
            {
                return PortalOAuthTokenResult.Failure("invalid_grant", "Authorization code was issued to a different client.");
            }
        }

        if (!string.IsNullOrWhiteSpace(record.RedirectUri))
        {
            if (string.IsNullOrWhiteSpace(request.RedirectUri))
            {
                return PortalOAuthTokenResult.Failure("invalid_grant", "redirect_uri is required because it was included in the authorization request.");
            }

            if (!string.Equals(record.RedirectUri, request.RedirectUri, StringComparison.Ordinal))
            {
                return PortalOAuthTokenResult.Failure("invalid_grant", "redirect_uri does not match the authorization request.");
            }
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

        // BH2-023/BH2-024 fix: the validation and the atomic consume are split into
        // two phases so that client_id binding failures do not silently destroy the
        // token, and so that only one concurrent caller can acquire the record under
        // refresh-token rotation.  The non-destructive read below is for validation
        // only; the atomic consume further down is the single authoritative gate.
        var rotate = _tokenOptions.OAuth2.RotateRefreshTokens;

        // Non-destructive read — does not remove the token.
        var record = await _store.GetRefreshTokenAsync(request.RefreshToken, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return PortalOAuthTokenResult.Failure("invalid_grant", "Refresh token is invalid or expired.");
        }

        // RFC 6749 §6: client_id binding check — performed before any destructive
        // operation so a mis-matched client_id does not consume the token.
        if (!string.IsNullOrWhiteSpace(record.ClientId))
        {
            if (string.IsNullOrWhiteSpace(request.ClientId))
            {
                return PortalOAuthTokenResult.Failure("invalid_grant", "client_id is required for this refresh token.");
            }

            if (!string.Equals(record.ClientId, request.ClientId, StringComparison.Ordinal))
            {
                return PortalOAuthTokenResult.Failure("invalid_grant", "Refresh token was issued to a different client.");
            }
        }

        if (!rotate)
        {
            // Rotation disabled (legacy ArcGIS behavior): the token is reusable, issue
            // without consuming it.
            return await IssueAsync(
                record.ClientId,
                record.Principal,
                requestedMinutes: null,
                requestBinding,
                includeRefreshToken: false,
                cancellationToken).ConfigureAwait(false);
        }

        // BH2-023: atomically consume the refresh token so only one concurrent caller
        // can acquire it. The non-destructive read above was for validation; this SET NX
        // backed consume is the authoritative single-use gate. A null result means a
        // concurrent caller already consumed the token (or it expired between the two reads).
        var consumed = await _store.ConsumeRefreshTokenAsync(request.RefreshToken, cancellationToken).ConfigureAwait(false);
        if (consumed is null)
        {
            return PortalOAuthTokenResult.Failure("invalid_grant", "Refresh token has already been used.");
        }

        // BH2-024: if IssueAsync throws (transient Redis/network failure) after the atomic
        // consume, restore the original token so the client can retry successfully instead
        // of being forced to re-authenticate. Restoration is best-effort: if the store is
        // also unavailable the client must re-authenticate, but the original exception is
        // always re-thrown so the caller receives the correct error signal.
        try
        {
            return await IssueAsync(
                consumed.ClientId,
                consumed.Principal,
                requestedMinutes: null,
                requestBinding,
                includeRefreshToken: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await _store.RestoreRefreshTokenAsync(
                    request.RefreshToken, consumed, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Intentional: this is a best-effort restore of the just-consumed refresh
                // token (BH2-024) so the client can retry; if the store is also unavailable
                // there is nothing actionable to do here, and the original IssueAsync
                // failure below (via `throw;`) is the real error signal that propagates to
                // the caller/telemetry, so a secondary log line here would be redundant.
            }
            throw;
        }
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

        var issuance = await MintAccessTokenAsync(
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
    bool IncludeRefreshToken,
    // client_credentials grant (ADR-0053): the client secret authenticates against
    // the Admin API-key store; scope may only narrow; ClientIp binds the token.
    string? ClientSecret = null,
    string? Scope = null,
    string? ClientIp = null);

/// <summary>Result of a token exchange, ready to serialize as the Esri envelope.</summary>
internal sealed record PortalOAuthTokenResult(
    bool Succeeded,
    string? AccessToken,
    long ExpiresInSeconds,
    string? RefreshToken,
    string? Error,
    string? ErrorDescription,
    // Space-delimited scopes actually granted (RFC 6749 §5.1), set for the
    // first-class client_credentials grant (#1888). Null when no scope applies.
    string? Scope = null)
{
    /// <summary>Builds a successful token result.</summary>
    public static PortalOAuthTokenResult Success(string accessToken, long expiresInSeconds, string? refreshToken, string? scope = null)
        => new(true, accessToken, expiresInSeconds, refreshToken, null, null, scope);

    /// <summary>Builds an OAuth2 error result.</summary>
    public static PortalOAuthTokenResult Failure(string error, string description)
        => new(false, null, 0, null, error, description);
}
