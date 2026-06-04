// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Infrastructure.Logging;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Brokers the ArcGIS OAuth2 named-user flow (#1242) onto the shared OIDC core
/// (ADR-0049). Honua acts as the OAuth2 authorization server that ArcGIS clients
/// (Pro "Add Portal", Field Maps) talk to, while delegating <em>identity</em> to
/// the operator's configured OIDC provider via the existing auth-code+PKCE
/// machinery (<see cref="OidcProviderCatalog"/> + discovery + the shared
/// <see cref="IPortalCredentialVerifier"/>). No parallel user store, token store,
/// or group→role mapping is introduced: the verified principal is the same one a
/// direct OIDC session yields, and access tokens are minted through
/// <see cref="IPortalTokenIssuer"/>.
/// </summary>
internal sealed class PortalOAuthBroker(
    IOptions<OidcAuthenticationOptions> oidcOptions,
    IPortalCredentialVerifier credentialVerifier,
    PortalOAuthStore store,
    IHttpClientFactory httpClientFactory,
    ILogger<PortalOAuthBroker> logger)
{
    /// <summary>Named HTTP client used for IdP discovery/token/JWKS calls.</summary>
    internal const string HttpClientName = "PortalOAuthOidc";

    private static readonly TimeSpan BrokerSessionLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AuthorizationCodeLifetime = TimeSpan.FromMinutes(5);

    private readonly OidcAuthenticationOptions _oidcOptions = oidcOptions.Value;
    private readonly IPortalCredentialVerifier _credentialVerifier = credentialVerifier;
    private readonly PortalOAuthStore _store = store;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<PortalOAuthBroker> _logger = logger;

    /// <summary>Whether the OIDC core is enabled and at least one valid provider exists.</summary>
    public bool IsConfigured =>
        _oidcOptions.Enabled &&
        OidcProviderCatalog.GetProviders(_oidcOptions).Any(static p => p.IsValid);

    /// <summary>
    /// Starts the authorization-code flow: resolves the OIDC provider, persists a
    /// broker session pinning the ArcGIS client's parameters, and returns the IdP
    /// authorize URL the browser should be redirected to.
    /// </summary>
    /// <returns>The provider IdP authorize URL, or <see langword="null"/> when no
    /// provider could be resolved or the IdP is unavailable.</returns>
    public async Task<string?> BeginAuthorizeAsync(
        PortalOAuthAuthorizeRequest request,
        Func<string, string> callbackUriFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callbackUriFactory);

        var provider = ResolveProvider(request.ProviderKey);
        if (provider is null)
        {
            PortalOAuthLog.AuthorizeRejected(_logger, "no valid OIDC provider");
            return null;
        }

        OidcDiscoveryDocument discovery;
        try
        {
            discovery = await GetDiscoveryDocumentAsync(provider.Authority!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            PortalOAuthLog.ProviderUnavailable(_logger, provider.Key, ex);
            return null;
        }

        if (string.IsNullOrWhiteSpace(discovery.AuthorizationEndpoint))
        {
            PortalOAuthLog.AuthorizeRejected(_logger, "provider has no authorization endpoint");
            return null;
        }

        var idpState = GenerateRandomString(32);
        var idpVerifier = GenerateRandomString(64);
        var idpChallenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(idpVerifier)));

        var session = new PortalOAuthBrokerSession
        {
            ProviderKey = provider.Key,
            ClientId = request.ClientId,
            RedirectUri = request.RedirectUri,
            ClientState = request.State,
            ClientCodeChallenge = request.CodeChallenge,
            ClientCodeChallengeMethod = request.CodeChallengeMethod,
            ExpirationMinutes = request.ExpirationMinutes,
            IdpState = idpState,
            IdpCodeVerifier = idpVerifier,
            ExpiresAt = DateTimeOffset.UtcNow.Add(BrokerSessionLifetime),
        };

        var brokerSessionId = await _store.CreateBrokerSessionAsync(session, cancellationToken).ConfigureAwait(false);

        // The IdP callback returns to Honua, carrying the broker session id in the
        // CSRF state so the callback can recover the pinned ArcGIS parameters
        // without a cookie (ArcGIS Pro's embedded browser is hostile to cookies).
        var combinedState = $"{idpState}.{brokerSessionId}";
        var callbackUri = callbackUriFactory(PortalOAuthRoutes.CallbackPath);

        return BuildAuthorizeUrl(discovery.AuthorizationEndpoint, provider, callbackUri, combinedState, idpChallenge);
    }

    /// <summary>
    /// Completes the IdP leg: exchanges the IdP authorization code, validates the
    /// ID token, projects the verified principal, and mints a Honua ArcGIS
    /// authorization code bound to the ArcGIS client's PKCE challenge.
    /// </summary>
    public async Task<PortalOAuthCallbackResult> CompleteCallbackAsync(
        string? combinedState,
        string? idpCode,
        Func<string, string> callbackUriFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callbackUriFactory);

        if (string.IsNullOrWhiteSpace(combinedState) || string.IsNullOrWhiteSpace(idpCode))
        {
            PortalOAuthLog.CallbackRejected(_logger, "missing state or code");
            return PortalOAuthCallbackResult.Failure("invalid_request", "Missing authorization response parameters.");
        }

        var separator = combinedState.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0 || separator >= combinedState.Length - 1)
        {
            PortalOAuthLog.CallbackRejected(_logger, "malformed state");
            return PortalOAuthCallbackResult.Failure("invalid_request", "Authorization state is malformed.");
        }

        var idpState = combinedState[..separator];
        var brokerSessionId = combinedState[(separator + 1)..];

        var session = await _store.ConsumeBrokerSessionAsync(brokerSessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || !CryptographicEquals(session.IdpState, idpState))
        {
            PortalOAuthLog.CallbackRejected(_logger, "unknown or expired broker session");
            return PortalOAuthCallbackResult.Failure("access_denied", "Authorization session is invalid or expired.");
        }

        var provider = ResolveProvider(session.ProviderKey);
        if (provider is null)
        {
            PortalOAuthLog.CallbackRejected(_logger, "provider no longer valid");
            return PortalOAuthCallbackResult.Failure("server_error", "Identity provider is unavailable.");
        }

        OidcDiscoveryDocument discovery;
        OidcIdpTokenResponse? idpTokens;
        try
        {
            discovery = await GetDiscoveryDocumentAsync(provider.Authority!, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(discovery.TokenEndpoint))
            {
                return PortalOAuthCallbackResult.Failure("server_error", "Identity provider is unavailable.");
            }

            idpTokens = await ExchangeIdpCodeAsync(
                discovery.TokenEndpoint,
                provider,
                idpCode,
                session.IdpCodeVerifier,
                callbackUriFactory(PortalOAuthRoutes.CallbackPath),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            PortalOAuthLog.ProviderUnavailable(_logger, provider.Key, ex);
            return PortalOAuthCallbackResult.Failure("server_error", "Identity provider is unavailable.");
        }

        if (idpTokens is null)
        {
            PortalOAuthLog.CallbackRejected(_logger, "IdP code exchange failed");
            return PortalOAuthCallbackResult.Failure("access_denied", "Authentication failed with the identity provider.");
        }

        // Relay the IdP id/access token through the shared portal credential
        // verifier (the OIDC-backed verifier validates issuer/audience/signing and
        // normalizes roles through OidcClaimsTransformation, exactly like a direct
        // Bearer session). This is the single identity seam mandated by ADR-0049.
        var idpToken = idpTokens.IdToken ?? idpTokens.AccessToken;
        if (string.IsNullOrWhiteSpace(idpToken))
        {
            return PortalOAuthCallbackResult.Failure("access_denied", "Identity provider returned no usable token.");
        }

        var verified = await _credentialVerifier
            .VerifyAsync(string.Empty, idpToken, cancellationToken)
            .ConfigureAwait(false);
        if (verified is null)
        {
            PortalOAuthLog.CallbackRejected(_logger, "principal verification failed");
            return PortalOAuthCallbackResult.Failure("access_denied", "Authentication failed.");
        }

        var code = new PortalOAuthAuthorizationCode
        {
            ClientId = session.ClientId,
            RedirectUri = session.RedirectUri,
            CodeChallenge = session.ClientCodeChallenge,
            CodeChallengeMethod = session.ClientCodeChallengeMethod,
            ExpirationMinutes = session.ExpirationMinutes,
            Principal = PortalOAuthPrincipal.From(verified),
            ExpiresAt = DateTimeOffset.UtcNow.Add(AuthorizationCodeLifetime),
        };

        var codeValue = await _store.CreateAuthorizationCodeAsync(code, cancellationToken).ConfigureAwait(false);
        if (_logger.IsEnabled(LogLevel.Information))
        {
#pragma warning disable CA1873 // LogValueRedactor.Hash only invoked inside the IsEnabled gate above.
            PortalOAuthLog.CodeIssued(_logger, LogValueRedactor.Hash(verified.PrincipalId), provider.Key);
#pragma warning restore CA1873
        }

        var redirect = QueryHelpers.AddQueryString(
            session.RedirectUri,
            new Dictionary<string, string?>
            {
                ["code"] = codeValue,
                ["state"] = session.ClientState,
            });

        return PortalOAuthCallbackResult.Success(redirect);
    }

    private OidcProviderRegistration? ResolveProvider(string? providerKey)
    {
        if (!_oidcOptions.Enabled)
        {
            return null;
        }

        var providers = OidcProviderCatalog.GetProviders(_oidcOptions).Where(static p => p.IsValid).ToArray();
        if (providers.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(providerKey) &&
            OidcProviderCatalog.TryResolveProvider(_oidcOptions, providerKey, out var resolved))
        {
            return resolved;
        }

        // ArcGIS clients do not send a provider hint; default to the single
        // configured provider. When several are configured the operator must pass
        // an explicit `provider` hint, otherwise the first valid one is used.
        return providers[0];
    }

    private async Task<OidcDiscoveryDocument> GetDiscoveryDocumentAsync(string authority, CancellationToken cancellationToken)
    {
        var url = authority.TrimEnd('/') + "/.well-known/openid-configuration";
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content
                .ReadFromJsonAsync(PortalOAuthBrokerJsonContext.Default.OidcDiscoveryDocument, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new InvalidOperationException("OIDC discovery document was empty.");
    }

    private async Task<OidcIdpTokenResponse?> ExchangeIdpCodeAsync(
        string tokenEndpoint,
        OidcProviderRegistration provider,
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = provider.ClientId!,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri,
        };

        if (!string.IsNullOrWhiteSpace(provider.ClientSecret))
        {
            form["client_secret"] = provider.ClientSecret;
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client
            .PostAsync(tokenEndpoint, new FormUrlEncodedContent(form), cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content
            .ReadFromJsonAsync(PortalOAuthBrokerJsonContext.Default.OidcIdpTokenResponse, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string BuildAuthorizeUrl(
        string authorizationEndpoint,
        OidcProviderRegistration provider,
        string redirectUri,
        string state,
        string codeChallenge)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = provider.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = string.Join(' ', provider.Scopes),
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };

        return QueryHelpers.AddQueryString(authorizationEndpoint, parameters);
    }

    private static string GenerateRandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var result = new char[length];
        for (var i = 0; i < length; i++)
        {
            result[i] = chars[bytes[i] % chars.Length];
        }

        return new string(result);
    }

    private static bool CryptographicEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b));

}

/// <summary>Subset of the OIDC discovery document the bridge consumes.</summary>
internal sealed class OidcDiscoveryDocument
{
    /// <summary>The provider authorization endpoint.</summary>
    [JsonPropertyName("authorization_endpoint")]
    public string AuthorizationEndpoint { get; init; } = string.Empty;

    /// <summary>The provider token endpoint.</summary>
    [JsonPropertyName("token_endpoint")]
    public string TokenEndpoint { get; init; } = string.Empty;
}

/// <summary>Subset of the IdP token response the bridge consumes.</summary>
internal sealed class OidcIdpTokenResponse
{
    /// <summary>The IdP-issued access token.</summary>
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    /// <summary>The IdP-issued ID token.</summary>
    [JsonPropertyName("id_token")]
    public string? IdToken { get; init; }
}

[JsonSerializable(typeof(OidcDiscoveryDocument))]
[JsonSerializable(typeof(OidcIdpTokenResponse))]
internal sealed partial class PortalOAuthBrokerJsonContext : JsonSerializerContext
{
}

/// <summary>Inputs parsed from <c>GET /sharing/rest/oauth2/authorize</c>.</summary>
internal sealed record PortalOAuthAuthorizeRequest(
    string ClientId,
    string RedirectUri,
    string? State,
    string? CodeChallenge,
    string? CodeChallengeMethod,
    string? ProviderKey,
    int? ExpirationMinutes);

/// <summary>Outcome of completing the IdP callback leg.</summary>
internal sealed record PortalOAuthCallbackResult(
    bool Succeeded,
    string? RedirectUri,
    string? Error,
    string? ErrorDescription)
{
    /// <summary>Builds a success result carrying the ArcGIS client redirect URL.</summary>
    public static PortalOAuthCallbackResult Success(string redirectUri)
        => new(true, redirectUri, null, null);

    /// <summary>Builds a failure result carrying an OAuth2 error code/description.</summary>
    public static PortalOAuthCallbackResult Failure(string error, string description)
        => new(false, null, error, description);
}

/// <summary>Canonical route paths for the ArcGIS OAuth2 bridge.</summary>
internal static class PortalOAuthRoutes
{
    /// <summary>The authorize entrypoint ArcGIS clients redirect the browser to.</summary>
    public const string AuthorizePath = "/sharing/rest/oauth2/authorize";

    /// <summary>The IdP return endpoint Honua registers with the OIDC provider.</summary>
    public const string CallbackPath = "/sharing/rest/oauth2/callback";

    /// <summary>The token endpoint ArcGIS clients exchange the code at.</summary>
    public const string TokenPath = "/sharing/rest/oauth2/token";
}
