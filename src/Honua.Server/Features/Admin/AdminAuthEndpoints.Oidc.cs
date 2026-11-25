// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace Honua.Server.Features.Admin;

internal static partial class AdminAuthEndpoints
{
    private enum RedirectHttpsPolicy
    {
        AllowHttp,
        RequireHttps
    }

    private sealed class AdminTokenExchangeContext
    {
        public required IHttpClientFactory HttpClientFactory { get; init; }
        public required string TokenEndpoint { get; init; }
        public required OidcProviderRegistration Provider { get; init; }
        public required AdminAuthTokenRequest Request { get; init; }
        public required string CodeVerifier { get; init; }
        public required string RedirectUri { get; init; }
    }

    private sealed class AdminAuthorizeUrlParameters
    {
        public required string AuthorizationEndpoint { get; init; }
        public required OidcProviderRegistration Provider { get; init; }
        public required string RedirectUri { get; init; }
        public required string State { get; init; }
        public required string Nonce { get; init; }
        public required string CodeChallenge { get; init; }
    }

    private sealed class AdminLogoutUrlContext
    {
        public required OidcProviderRegistration Provider { get; init; }
        public string? IdTokenHint { get; init; }
        public required RedirectHttpsPolicy HttpsPolicy { get; init; }
        public required HttpContext HttpContext { get; init; }
        public required IHttpClientFactory HttpClientFactory { get; init; }
        public required ILogger<AdminAuthEndpointsLog> Logger { get; init; }
    }

    private sealed class AdminTokenSessionInput
    {
        public required OidcProviderRegistration Provider { get; init; }
        public required AdminAuthTokenResponse TokenResponse { get; init; }
        public required IReadOnlyList<Claim> ValidatedClaims { get; init; }
        public required IReadOnlyList<AdminAuthSessionClaim> SessionClaims { get; init; }
        public string? PendingSessionId { get; init; }
    }

    private static async Task<AdminAuthOidcDiscoveryDocument> GetDiscoveryDocumentAsync(
        IHttpClientFactory httpClientFactory,
        string authority,
        CancellationToken cancellationToken)
    {
        var discoveryUrl = authority.TrimEnd('/') + "/.well-known/openid-configuration";
        var client = httpClientFactory.CreateClient(AdminAuthHttpClient);
        using var response = await client.GetAsync(discoveryUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync(
                AdminAuthJsonContext.Default.AdminAuthOidcDiscoveryDocument,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("OIDC discovery document response was empty.");
    }

    private static async Task<IResult> PersistInteractiveOidcSessionAsync(
        HttpContext context,
        AdminAuthSessionStore sessionStore,
        AdminTokenSessionInput input)
    {
        var now = DateTimeOffset.UtcNow;
        var sessionExpiresAt = ResolveSessionExpiration(input.TokenResponse.ExpiresIn, input.ValidatedClaims, now);
        var cookieLifetime = sessionExpiresAt - now;
        if (cookieLifetime <= TimeSpan.Zero)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(
                context,
                "Identity provider is temporarily unavailable.",
                retryable: true);
        }

        var authSessionId = await sessionStore.CreateAuthenticatedSessionAsync(
            new AdminAuthAuthenticatedSession
            {
                ProviderKey = input.Provider.Key,
                AccessToken = input.TokenResponse.AccessToken!,
                IdToken = input.TokenResponse.IdToken,
                Claims = input.SessionClaims.ToArray(),
                ExpiresAt = sessionExpiresAt,
                Origin = AdminAuthSessionOrigin.InteractiveOidc
            }, context.RequestAborted).ConfigureAwait(false);

        SetCookie(
            context.Response,
            AdminAuthSessionStore.AuthSessionCookieName,
            authSessionId,
            CreateAdminAuthCookieOptions(context, cookieLifetime));

        await CleanupPendingSessionAsync(context, sessionStore, input.PendingSessionId).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static async Task CleanupPendingSessionAsync(
        HttpContext context,
        AdminAuthSessionStore sessionStore,
        string? pendingSessionId)
    {
        if (!string.IsNullOrWhiteSpace(pendingSessionId))
        {
            await sessionStore.RemovePendingSessionAsync(pendingSessionId, context.RequestAborted).ConfigureAwait(false);
        }

        DeleteCookie(context.Response, AdminAuthSessionStore.PendingSessionCookieName);
    }

    private static RedirectHttpsPolicy ResolveRedirectHttpsPolicy(OidcAuthenticationOptions options)
        => options.RequireHttps ? RedirectHttpsPolicy.RequireHttps : RedirectHttpsPolicy.AllowHttp;

    private static async Task<HttpResponseMessage> RequestTokenAsync(
        AdminTokenExchangeContext exchange,
        CancellationToken cancellationToken)
    {
        var formValues = new Dictionary<string, string>
        {
            ["grant_type"] = AuthorizationCodeGrantType,
            ["code"] = exchange.Request.Code!,
            ["code_verifier"] = exchange.CodeVerifier,
            ["redirect_uri"] = exchange.RedirectUri,
            ["client_id"] = exchange.Provider.ClientId!
        };

        if (!string.IsNullOrWhiteSpace(exchange.Provider.ClientSecret))
        {
            formValues["client_secret"] = exchange.Provider.ClientSecret;
        }

        var client = exchange.HttpClientFactory.CreateClient(AdminAuthHttpClient);
        using var content = new FormUrlEncodedContent(formValues);
        return await client.PostAsync(exchange.TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildAuthorizeUrl(AdminAuthorizeUrlParameters parameters)
    {
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = parameters.Provider.ClientId,
            ["redirect_uri"] = parameters.RedirectUri,
            ["scope"] = string.Join(" ", parameters.Provider.Scopes),
            ["state"] = parameters.State,
            ["nonce"] = parameters.Nonce,
            ["code_challenge"] = parameters.CodeChallenge,
            ["code_challenge_method"] = "S256"
        };

        return QueryHelpers.AddQueryString(parameters.AuthorizationEndpoint, query);
    }

    private static async Task<string?> TryBuildLogoutUrlAsync(AdminLogoutUrlContext logoutContext)
    {
        try
        {
            var logoutEndpoint = await ResolveLogoutEndpointAsync(logoutContext).ConfigureAwait(false);
            var isValidLogoutEndpoint = logoutContext.HttpsPolicy == RedirectHttpsPolicy.RequireHttps
                ? PortalOAuthBroker.IsValidHttpsBrowserRedirectEndpoint(logoutEndpoint)
                : PortalOAuthBroker.IsValidHttpOrHttpsBrowserRedirectEndpoint(logoutEndpoint);
            if (!isValidLogoutEndpoint)
            {
                return null;
            }

            return BuildLogoutRedirectUrl(logoutEndpoint!, logoutContext);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            AdminAuthLog.CreateLogoutUrlFailed(logoutContext.Logger, logoutContext.Provider.Key, ex);
            return null;
        }
    }

    private static async Task<string?> ResolveLogoutEndpointAsync(AdminLogoutUrlContext logoutContext)
    {
        if (!string.IsNullOrWhiteSpace(logoutContext.Provider.LogoutEndpoint))
        {
            return logoutContext.Provider.LogoutEndpoint;
        }

        var discovery = await GetDiscoveryDocumentAsync(
            logoutContext.HttpClientFactory,
            logoutContext.Provider.Authority!,
            logoutContext.HttpContext.RequestAborted).ConfigureAwait(false);
        return discovery.EndSessionEndpoint;
    }

    private static string BuildLogoutRedirectUrl(string logoutEndpoint, AdminLogoutUrlContext logoutContext)
    {
        var redirectUri = BuildAbsoluteUri(logoutContext.HttpContext, AdminPostLogoutRedirectPath);
        var parameters = logoutContext.Provider.UseCognitoLogout
            ? new Dictionary<string, string?>
            {
                ["client_id"] = logoutContext.Provider.ClientId,
                ["logout_uri"] = redirectUri
            }
            : new Dictionary<string, string?>
            {
                ["post_logout_redirect_uri"] = redirectUri
            };

        if (!logoutContext.Provider.UseCognitoLogout && !string.IsNullOrWhiteSpace(logoutContext.IdTokenHint))
        {
            parameters["id_token_hint"] = logoutContext.IdTokenHint;
        }

        return QueryHelpers.AddQueryString(logoutEndpoint, parameters);
    }

    private static string GenerateRandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var result = new char[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = chars[bytes[i] % chars.Length];
        }

        return new string(result);
    }
}
