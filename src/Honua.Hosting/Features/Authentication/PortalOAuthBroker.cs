// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;

namespace Honua.Infrastructure.Authentication;

internal sealed partial class PortalOAuthBroker(PortalOAuthBrokerDependencies dependencies)
{
    internal const string HttpClientName = "PortalOAuthOidc";

    private static readonly TimeSpan BrokerSessionLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AuthorizationCodeLifetime = TimeSpan.FromMinutes(5);

    private readonly OidcAuthenticationOptions _oidcOptions = dependencies.OidcOptions.Value;
    private readonly OidcPortalCredentialVerifier _oidcCredentialVerifier = dependencies.OidcCredentialVerifier;
    private readonly PortalOAuthStore _store = dependencies.Store;
    private readonly IHttpClientFactory _httpClientFactory = dependencies.HttpClientFactory;
    private readonly ILogger<PortalOAuthBroker> _logger = dependencies.Logger;
    private readonly IOidcRuntimeProviderSource? _runtimeProviders = dependencies.RuntimeProviders;
    private readonly DiscoveryEndpointPolicy _discoveryPolicy =
        BuildDiscoveryPolicy(dependencies.OidcOptions.Value, dependencies.HostEnvironment);

    public bool IsConfigured =>
        _oidcOptions.Enabled &&
        OidcProviderCatalog.GetProviders(_oidcOptions, _runtimeProviders).Any(static p => p.IsValid);

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

        var validRedirect = _oidcOptions.RequireHttps
            ? IsValidHttpsBrowserRedirectEndpoint(discovery.AuthorizationEndpoint)
            : IsValidHttpOrHttpsBrowserRedirectEndpoint(discovery.AuthorizationEndpoint);
        if (!validRedirect)
        {
            PortalOAuthLog.AuthorizeRejected(_logger, "provider has no valid authorization endpoint");
            return null;
        }

        var (session, idpChallenge) = CreateBrokerSession(provider, request);
        var brokerSessionId = await _store.CreateBrokerSessionAsync(session, cancellationToken).ConfigureAwait(false);
        var combinedState = $"{session.IdpState}.{brokerSessionId}";
        var callbackUri = callbackUriFactory(PortalOAuthRoutes.CallbackPath);

        return BuildAuthorizeUrl(new AuthorizeUrlRequest
        {
            AuthorizationEndpoint = discovery.AuthorizationEndpoint,
            Provider = provider,
            RedirectUri = callbackUri,
            State = combinedState,
            Nonce = session.IdpNonce!,
            CodeChallenge = idpChallenge,
        });
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

        var (session, sessionFailure) = await ConsumeBrokerSessionAsync(combinedState, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return sessionFailure!;
        }

        var provider = ResolveProvider(session.ProviderKey);
        if (provider is null)
        {
            PortalOAuthLog.CallbackRejected(_logger, "provider no longer valid");
            return ClientRedirectFailure(session, "server_error", "Identity provider is unavailable.");
        }

        var exchange = new CallbackTokenExchange
        {
            Session = session,
            Provider = provider,
            IdpCode = idpCode,
            CallbackUri = callbackUriFactory(PortalOAuthRoutes.CallbackPath),
        };
        var (idpTokens, exchangeFailure) = await ExchangeValidatedIdpCodeAsync(exchange, cancellationToken).ConfigureAwait(false);
        if (idpTokens is null)
        {
            return exchangeFailure!;
        }

        var (verified, verifyFailure) = await VerifyCallbackIdentityAsync(session, idpTokens.IdToken, cancellationToken).ConfigureAwait(false);
        return verified is null
            ? verifyFailure!
            : await IssueAuthorizationCodeRedirectAsync(session, new VerifiedCallbackIdentity(verified, provider.Key), cancellationToken).ConfigureAwait(false);
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

        var (session, failure) = await ConsumeBrokerSessionAsync(combinedState, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return failure!;
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

        var providers = OidcProviderCatalog.GetProviders(_oidcOptions, _runtimeProviders).Where(static p => p.IsValid).ToArray();
        if (providers.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(providerKey) &&
            OidcProviderCatalog.TryResolveProvider(_oidcOptions, providerKey, out var resolved, _runtimeProviders))
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

    private static string BuildAuthorizeUrl(AuthorizeUrlRequest request)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = request.Provider.ClientId,
            ["redirect_uri"] = request.RedirectUri,
            ["scope"] = string.Join(' ', request.Provider.Scopes),
            ["state"] = request.State,
            ["nonce"] = request.Nonce,
            ["code_challenge"] = request.CodeChallenge,
            ["code_challenge_method"] = "S256",
        };

        return QueryHelpers.AddQueryString(request.AuthorizationEndpoint, parameters);
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
