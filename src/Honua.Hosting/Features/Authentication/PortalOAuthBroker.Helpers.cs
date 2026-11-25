// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.Core.Features.Infrastructure.Validation;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Infrastructure.Authentication;

[Flags]
internal enum DiscoveryEndpointPolicy
{
    Strict = 0,
    AllowHttp = 1,
    AllowPrivateNetworks = 2,
}

internal sealed class PortalOAuthBrokerDependencies
{
    public required IOptions<OidcAuthenticationOptions> OidcOptions { get; init; }

    public required OidcPortalCredentialVerifier OidcCredentialVerifier { get; init; }

    public required PortalOAuthStore Store { get; init; }

    public required IHttpClientFactory HttpClientFactory { get; init; }

    public required ILogger<PortalOAuthBroker> Logger { get; init; }

    public IOidcRuntimeProviderSource? RuntimeProviders { get; init; }

    public IHostEnvironment? HostEnvironment { get; init; }
}

internal sealed partial class PortalOAuthBroker
{
    private async Task<(PortalOAuthBrokerSession? Session, PortalOAuthCallbackResult? Failure)> ConsumeBrokerSessionAsync(
        string combinedState,
        CancellationToken cancellationToken)
    {
        // Split on the LAST '.' for existing sessions whose idpState used the RFC
        // 3986 unreserved alphabet, including '.'. Newly minted state and the
        // brokerSessionId are lowercase hex, but accepting the earlier format
        // avoids invalidating a login already in progress during an upgrade.
        var separator = combinedState.LastIndexOf('.');
        if (separator <= 0 || separator >= combinedState.Length - 1)
        {
            PortalOAuthLog.CallbackRejected(_logger, "malformed state");
            return (null, PortalOAuthCallbackResult.Failure("invalid_request", "Authorization state is malformed."));
        }

        var idpState = combinedState[..separator];
        var brokerSessionId = combinedState[(separator + 1)..];
        var session = await _store.ConsumeBrokerSessionAsync(brokerSessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || !CryptographicEquals(session.IdpState, idpState))
        {
            PortalOAuthLog.CallbackRejected(_logger, "unknown or expired broker session");
            return (null, PortalOAuthCallbackResult.Failure("access_denied", "Authorization session is invalid or expired."));
        }

        return (session, null);
    }

    private async Task<(OidcIdpTokenResponse? Tokens, PortalOAuthCallbackResult? Failure)> ExchangeValidatedIdpCodeAsync(
        CallbackTokenExchange exchange,
        CancellationToken cancellationToken)
    {
        OidcIdpTokenResponse? idpTokens;
        try
        {
            var discovery = await GetDiscoveryDocumentAsync(exchange.Provider.Authority!, cancellationToken).ConfigureAwait(false);
            if (!await IsValidDiscoveryEndpointAsync(discovery.TokenEndpoint, _discoveryPolicy, cancellationToken).ConfigureAwait(false))
            {
                return (null, ClientRedirectFailure(exchange.Session, "server_error", "Identity provider is unavailable."));
            }

            idpTokens = await ExchangeIdpCodeAsync(
                discovery.TokenEndpoint,
                exchange.Provider,
                exchange.IdpCode,
                exchange.Session.IdpCodeVerifier,
                exchange.CallbackUri,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            PortalOAuthLog.ProviderUnavailable(_logger, exchange.Provider.Key, ex);
            return (null, ClientRedirectFailure(exchange.Session, "server_error", "Identity provider is unavailable."));
        }

        if (idpTokens is null)
        {
            PortalOAuthLog.CallbackRejected(_logger, "IdP code exchange failed");
            return (null, ClientRedirectFailure(exchange.Session, "access_denied", "Authentication failed with the identity provider."));
        }

        return (idpTokens, null);
    }

    // The bridge already exchanged an IdP code. Validate the ID token with the
    // OIDC verifier directly and verify its nonce matches the broker session.
    private async Task<(PortalCredentialPrincipal? Principal, PortalOAuthCallbackResult? Failure)> VerifyCallbackIdentityAsync(
        PortalOAuthBrokerSession session,
        string? idpToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idpToken))
        {
            return (null, ClientRedirectFailure(session, "access_denied", "Identity provider returned no usable token."));
        }

        var verified = await _oidcCredentialVerifier
            .VerifyForProviderAsync(session.ProviderKey, idpToken, cancellationToken)
            .ConfigureAwait(false);
        if (verified is null)
        {
            PortalOAuthLog.CallbackRejected(_logger, "principal verification failed");
            return (null, ClientRedirectFailure(session, "access_denied", "Authentication failed."));
        }

        if (string.IsNullOrWhiteSpace(session.IdpNonce) ||
            ReadSingleNonceClaim(idpToken) is not { } tokenNonce ||
            !CryptographicEquals(session.IdpNonce, tokenNonce))
        {
            PortalOAuthLog.CallbackRejected(_logger, "ID token nonce verification failed");
            return (null, ClientRedirectFailure(session, "access_denied", "Authentication failed."));
        }

        return (verified, null);
    }

    private async Task<PortalOAuthCallbackResult> IssueAuthorizationCodeRedirectAsync(
        PortalOAuthBrokerSession session,
        VerifiedCallbackIdentity identity,
        CancellationToken cancellationToken)
    {
        var code = new PortalOAuthAuthorizationCode
        {
            ClientId = session.ClientId,
            RedirectUri = session.RedirectUri,
            CodeChallenge = session.ClientCodeChallenge,
            CodeChallengeMethod = session.ClientCodeChallengeMethod,
            ExpirationMinutes = session.ExpirationMinutes,
            Principal = PortalOAuthPrincipal.From(identity.Principal),
            ExpiresAt = DateTimeOffset.UtcNow.Add(AuthorizationCodeLifetime),
        };

        var codeValue = await _store.CreateAuthorizationCodeAsync(code, cancellationToken).ConfigureAwait(false);
        if (_logger.IsEnabled(LogLevel.Information))
        {
#pragma warning disable CA1873 // LogValueRedactor.Hash only invoked inside the IsEnabled gate above.
            PortalOAuthLog.CodeIssued(_logger, LogValueRedactor.Hash(identity.Principal.PrincipalId), identity.ProviderKey);
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

    private static string? ReadSingleNonceClaim(string token)
    {
        var handler = new JsonWebTokenHandler();
        if (!handler.CanReadToken(token))
        {
            return null;
        }

        JsonWebToken jwt;
        try
        {
            jwt = handler.ReadJsonWebToken(token);
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityTokenException)
        {
            return null;
        }

        string? found = null;
        foreach (var claim in jwt.Claims)
        {
            if (!string.Equals(claim.Type, JwtRegisteredClaimNames.Nonce, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                return null;
            }

            found = claim.Value;
        }

        return string.IsNullOrWhiteSpace(found) ? null : found;
    }

    internal static async Task<bool> IsValidDiscoveryEndpointAsync(
        string? url,
        DiscoveryEndpointPolicy policy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var allowPrivateNetworks = (policy & DiscoveryEndpointPolicy.AllowPrivateNetworks) != 0;
        var requireHttps = (policy & DiscoveryEndpointPolicy.AllowHttp) == 0;
        var validation = await OutboundHttpUrlValidator
            .ValidateAsync(url, allowPrivateNetworks, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsValid || validation.Uri is null)
        {
            return false;
        }

        return !requireHttps || string.Equals(validation.Uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsValidHttpsBrowserRedirectEndpoint(string? url)
        => ParseBrowserRedirectUri(url) is { } uri
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    internal static bool IsValidHttpOrHttpsBrowserRedirectEndpoint(string? url)
        => ParseBrowserRedirectUri(url) is { } uri
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));

    private static Uri? ParseBrowserRedirectUri(string? url)
        => !string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && string.IsNullOrEmpty(uri.UserInfo)
            ? uri
            : null;

    private static DiscoveryEndpointPolicy BuildDiscoveryPolicy(
        OidcAuthenticationOptions options,
        IHostEnvironment? hostEnvironment)
    {
        var policy = options.RequireHttps ? DiscoveryEndpointPolicy.Strict : DiscoveryEndpointPolicy.AllowHttp;
        if (hostEnvironment is not null && (hostEnvironment.IsDevelopment() || hostEnvironment.IsEnvironment("Test")))
        {
            policy |= DiscoveryEndpointPolicy.AllowPrivateNetworks;
        }

        return policy;
    }

    internal static bool CryptographicEquals(string? a, string? b)
        => a is not null
            && b is not null
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(a),
                Encoding.UTF8.GetBytes(b));

    private sealed class AuthorizeUrlRequest
    {
        public required string AuthorizationEndpoint { get; init; }

        public required OidcProviderRegistration Provider { get; init; }

        public required string RedirectUri { get; init; }

        public required string State { get; init; }

        public required string Nonce { get; init; }

        public required string CodeChallenge { get; init; }
    }

    private sealed class CallbackTokenExchange
    {
        public required PortalOAuthBrokerSession Session { get; init; }

        public required OidcProviderRegistration Provider { get; init; }

        public required string IdpCode { get; init; }

        public required string CallbackUri { get; init; }
    }

    private static (PortalOAuthBrokerSession Session, string IdpChallenge) CreateBrokerSession(
        OidcProviderRegistration provider,
        PortalOAuthAuthorizeRequest request)
    {
        var idpState = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var idpNonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
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
            IdpNonce = idpNonce,
            ExpiresAt = DateTimeOffset.UtcNow.Add(BrokerSessionLifetime),
        };
        return (session, idpChallenge);
    }

    private readonly record struct VerifiedCallbackIdentity(PortalCredentialPrincipal Principal, string ProviderKey);
}
