// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Logging;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

internal sealed class PortalOAuthBroker(
    IOptions<OidcAuthenticationOptions> oidcOptions,
    OidcPortalCredentialVerifier oidcCredentialVerifier,
    PortalOAuthStore store,
    IHttpClientFactory httpClientFactory,
    ILogger<PortalOAuthBroker> logger,
    IOidcRuntimeProviderSource? runtimeProviders = null)
{
    internal const string HttpClientName = "PortalOAuthOidc";

    private static readonly TimeSpan BrokerSessionLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AuthorizationCodeLifetime = TimeSpan.FromMinutes(5);

    private readonly OidcAuthenticationOptions _oidcOptions = oidcOptions.Value;
    private readonly OidcPortalCredentialVerifier _oidcCredentialVerifier = oidcCredentialVerifier;
    private readonly PortalOAuthStore _store = store;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<PortalOAuthBroker> _logger = logger;

    public bool IsConfigured =>
        _oidcOptions.Enabled &&
        OidcProviderCatalog.GetProviders(_oidcOptions, runtimeProviders).Any(static p => p.IsValid);

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

        // State crosses the server's query-input scanner on the callback. Hex retains
        // all 256 random bits without the SQL-comment punctuation (for example "--")
        // that the PKCE unreserved alphabet can generate by chance.
        var idpState = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
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
        //
        // CSRF posture (#1484, see ADR-0049 "CSRF posture"): the cookieless binding
        // is judged adequate. `idpState` (256 random bits) plus `brokerSessionId`
        // (256-bit cache-key entropy) are both unguessable; the callback consumes the
        // session single-use by id and constant-time-compares `idpState` before
        // proceeding. The secret lives server-side in the single-use, 15-minute
        // broker session, so the cookieless transport does not weaken the standard
        // state-parameter binding. The ArcGIS-facing code is additionally bound to
        // the client's hard-required PKCE challenge and allow-listed exact
        // redirect_uri/client_id, so a code minted on a victim's behalf cannot be
        // redeemed by an attacker.
        var combinedState = $"{idpState}.{brokerSessionId}";
        var callbackUri = callbackUriFactory(PortalOAuthRoutes.CallbackPath);

        return BuildAuthorizeUrl(discovery.AuthorizationEndpoint, provider, callbackUri, combinedState, idpChallenge);
    }

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

        // Split on the LAST '.' for existing sessions whose idpState used the RFC
        // 3986 unreserved alphabet, including '.'. Newly minted state and the
        // brokerSessionId are lowercase hex, but accepting the earlier format
        // avoids invalidating a login already in progress during an upgrade.
        var separator = combinedState.LastIndexOf('.');
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
            return ClientRedirectFailure(session, "server_error", "Identity provider is unavailable.");
        }

        OidcDiscoveryDocument discovery;
        OidcIdpTokenResponse? idpTokens;
        try
        {
            discovery = await GetDiscoveryDocumentAsync(provider.Authority!, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(discovery.TokenEndpoint))
            {
                return ClientRedirectFailure(session, "server_error", "Identity provider is unavailable.");
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
            return ClientRedirectFailure(session, "server_error", "Identity provider is unavailable.");
        }

        if (idpTokens is null)
        {
            PortalOAuthLog.CallbackRejected(_logger, "IdP code exchange failed");
            return ClientRedirectFailure(session, "access_denied", "Authentication failed with the identity provider.");
        }

        // The bridge already exchanged an IdP code. Validate that token with the
        // OIDC verifier directly. UseOidc selects the verifier only for password
        // and generateToken checks, and leaving it false must not turn a configured
        // bridge callback into an admin-password rejection (honua-server#5424).
        var idpToken = idpTokens.IdToken ?? idpTokens.AccessToken;
        if (string.IsNullOrWhiteSpace(idpToken))
        {
            return ClientRedirectFailure(session, "access_denied", "Identity provider returned no usable token.");
        }

        var verified = await _oidcCredentialVerifier
            .VerifyForProviderAsync(session.ProviderKey, idpToken, cancellationToken)
            .ConfigureAwait(false);
        if (verified is null)
        {
            PortalOAuthLog.CallbackRejected(_logger, "principal verification failed");
            return ClientRedirectFailure(session, "access_denied", "Authentication failed.");
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

    // The broker session is consumed only when its state matches, so an IdP error cannot choose the client redirect.
    public async Task<PortalOAuthCallbackResult> RedirectIdpErrorAsync(
        string? combinedState,
        string? idpError,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(combinedState))
        {
            PortalOAuthLog.CallbackRejected(_logger, "missing state or code");
            return PortalOAuthCallbackResult.Failure("invalid_request", "Missing authorization response parameters.");
        }

        var separator = combinedState.LastIndexOf('.');
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

        return ClientRedirectFailure(
            session,
            NormalizeIdpError(idpError),
            "Authentication failed with the identity provider.");
    }

    private static string NormalizeIdpError(string? idpError)
        => idpError switch
        {
            "access_denied" or "invalid_request" or "unauthorized_client"
                or "server_error" or "temporarily_unavailable" => idpError,
            _ => "access_denied",
        };

    private static PortalOAuthCallbackResult ClientRedirectFailure(
        PortalOAuthBrokerSession session,
        string error,
        string description)
    {
        var redirect = QueryHelpers.AddQueryString(
            session.RedirectUri,
            new Dictionary<string, string?>
            {
                ["error"] = error,
                ["error_description"] = description,
                ["state"] = session.ClientState,
            });
        return new PortalOAuthCallbackResult(false, redirect, error, description);
    }

    private OidcProviderRegistration? ResolveProvider(string? providerKey)
    {
        if (!_oidcOptions.Enabled)
        {
            return null;
        }

        var providers = OidcProviderCatalog.GetProviders(_oidcOptions, runtimeProviders).Where(static p => p.IsValid).ToArray();
        if (providers.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(providerKey) &&
            OidcProviderCatalog.TryResolveProvider(_oidcOptions, providerKey, out var resolved, runtimeProviders))
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
        using var content = new FormUrlEncodedContent(form);
        using var response = await client
            .PostAsync(tokenEndpoint, content, cancellationToken)
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

internal sealed class OidcDiscoveryDocument
{
    [JsonPropertyName("authorization_endpoint")]
    public string AuthorizationEndpoint { get; init; } = string.Empty;

    [JsonPropertyName("token_endpoint")]
    public string TokenEndpoint { get; init; } = string.Empty;
}

internal sealed class OidcIdpTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; init; }
}

[JsonSerializable(typeof(OidcDiscoveryDocument))]
[JsonSerializable(typeof(OidcIdpTokenResponse))]
internal sealed partial class PortalOAuthBrokerJsonContext : JsonSerializerContext
{
}

internal sealed record PortalOAuthAuthorizeRequest(
    string ClientId,
    string RedirectUri,
    string? State,
    string? CodeChallenge,
    string? CodeChallengeMethod,
    string? ProviderKey,
    int? ExpirationMinutes);

internal sealed record PortalOAuthCallbackResult(
    bool Succeeded,
    string? RedirectUri,
    string? Error,
    string? ErrorDescription)
{
    public static PortalOAuthCallbackResult Success(string redirectUri)
        => new(true, redirectUri, null, null);

    public static PortalOAuthCallbackResult Failure(string error, string description)
        => new(false, null, error, description);
}

internal static class PortalOAuthRoutes
{
    public const string AuthorizePath = "/sharing/rest/oauth2/authorize";

    public const string CallbackPath = "/sharing/rest/oauth2/callback";

    public const string TokenPath = "/sharing/rest/oauth2/token";

    public const string IntrospectPath = "/sharing/rest/oauth2/introspect";

    public const string RevokePath = "/sharing/rest/oauth2/revoke";
}
