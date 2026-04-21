// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Anonymous admin auth bootstrap endpoints for the hosted admin UI.
/// Provider connection details stay server-side; the client receives only provider labels
/// and uses backend-assisted login, token exchange, and logout discovery.
/// </summary>
internal static class AdminAuthEndpoints
{
    private const string AdminAuthHttpClient = "AdminAuthOidc";
    private const string AuthorizationCodeGrantType = "authorization_code";
    private const string AdminRedirectPath = "/admin/auth/callback";
    private const string AdminPostLogoutRedirectPath = "/admin";
    private const int MaxStateLength = 512;
    private static readonly TimeSpan PendingSessionLifetime = TimeSpan.FromMinutes(15);

    internal sealed class AdminAuthEndpointsLog;

    /// <summary>
    /// Registers the admin auth configuration and backend-assisted OIDC flow endpoints.
    /// </summary>
    public static void MapAdminAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/auth")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin Auth")
            .AllowAnonymous();

        _ = group.MapGet("/config", HandleGetAuthConfig)
            .WithDisplayName("Get Admin Auth Configuration")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.MapGet("/session", HandleGetSession)
            .WithDisplayName("Get Admin Auth Session")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.MapPost("/providers/{providerKey}/authorize-url", HandleCreateAuthorizeUrl)
            .WithDisplayName("Create Admin Auth Authorize Url")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .WithMetadata(new RateLimitAttribute(5)); // 5 requests per minute for auth initiation

        _ = group.MapPost("/providers/{providerKey}/token", HandleRequestToken)
            .WithDisplayName("Request Admin Auth Token")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .WithMetadata(new RateLimitAttribute(5)); // 5 requests per minute for token exchange

        _ = group.MapPost("/logout", HandleLogout)
            .WithDisplayName("Logout Admin Auth Session")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        _ = group.MapGet("/providers/{providerKey}/logout-url", HandleGetLogoutUrl)
            .WithDisplayName("Get Admin Auth Logout Url")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static Microsoft.AspNetCore.Http.HttpResults.Ok<AdminAuthConfigResponse> HandleGetAuthConfig(
        [FromServices] IOptions<OidcAuthenticationOptions> oidcOptions,
        [FromServices] ILogger<AdminAuthEndpointsLog> logger)
    {
        var providers = oidcOptions.Value.Enabled
            ? OidcProviderCatalog.GetProviders(oidcOptions.Value)
                .Where(static provider => provider.IsValid)
                .Select(static provider => new AdminAuthProviderInfo
                {
                    Key = provider.Key,
                    DisplayName = provider.DisplayName
                })
                .ToList()
            : [];
        AdminAuthLog.AuthConfigServed(logger, providers.Count);

        var response = new AdminAuthConfigResponse
        {
            OidcEnabled = oidcOptions.Value.Enabled && providers.Count > 0,
            Providers = providers
        };

        return TypedResults.Ok(response);
    }

    private static async Task<Microsoft.AspNetCore.Http.HttpResults.Ok<AdminAuthSessionResponse>> HandleGetSession(
        HttpContext context,
        [FromServices] AdminAuthSessionStore sessionStore)
    {
        var sessionId = context.Request.Cookies[AdminAuthSessionStore.AuthSessionCookieName];
        var session = await sessionStore.GetAuthenticatedSessionAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
        if (session is null)
        {
            DeleteCookie(context.Response, AdminAuthSessionStore.AuthSessionCookieName);
            return TypedResults.Ok(new AdminAuthSessionResponse());
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            await sessionStore.RemoveAuthenticatedSessionAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
            DeleteCookie(context.Response, AdminAuthSessionStore.AuthSessionCookieName);
            return TypedResults.Ok(new AdminAuthSessionResponse());
        }

        return TypedResults.Ok(new AdminAuthSessionResponse
        {
            IsAuthenticated = true,
            ProviderKey = session.ProviderKey,
            ExpiresAt = session.ExpiresAt,
            Claims = context.User.Claims
                .Select(claim => new AdminAuthClaimInfo
                {
                    Type = claim.Type,
                    Value = claim.Value
                })
                .ToList()
        });
    }

    private static async Task<IResult> HandleCreateAuthorizeUrl(
        string providerKey,
        AdminAuthAuthorizeUrlRequest _,
        HttpContext context,
        [FromServices] IOptions<OidcAuthenticationOptions> oidcOptions,
        [FromServices] AdminAuthSessionStore sessionStore,
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] ILogger<AdminAuthEndpointsLog> logger)
    {
        if (!OidcProviderCatalog.TryResolveProvider(oidcOptions.Value, providerKey, out var provider))
        {
            return StandardErrorHelpers.CreateNotFound(context, "Identity provider was not found.");
        }

        var baseUrlRequirement = RequireConfiguredPublicBaseUrl(context);
        if (baseUrlRequirement != null)
        {
            return baseUrlRequirement;
        }

        try
        {
            var discovery = await GetDiscoveryDocumentAsync(
                httpClientFactory,
                provider.Authority!,
                context.RequestAborted).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(discovery.AuthorizationEndpoint))
            {
                logger.LogWarning("OIDC provider {ProviderKey} did not return an authorization endpoint.", provider.Key);
                return StandardErrorHelpers.CreateServiceUnavailable(context, "Identity provider is temporarily unavailable.");
            }

            var state = GenerateRandomString(32);
            var verifier = GenerateRandomString(64);
            var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var pendingSessionId = await sessionStore.CreatePendingSessionAsync(
                provider.Key,
                state,
                verifier,
                DateTimeOffset.UtcNow.Add(PendingSessionLifetime),
                context.RequestAborted).ConfigureAwait(false);

            if (context.Request.Cookies.TryGetValue(AdminAuthSessionStore.AuthSessionCookieName, out var existingSessionId))
            {
                await sessionStore.RemoveAuthenticatedSessionAsync(existingSessionId, context.RequestAborted).ConfigureAwait(false);
                DeleteCookie(context.Response, AdminAuthSessionStore.AuthSessionCookieName);
            }

            SetCookie(
                context.Response,
                AdminAuthSessionStore.PendingSessionCookieName,
                pendingSessionId,
                CreateAdminAuthCookieOptions(context, PendingSessionLifetime));

            var authorizeUrl = BuildAuthorizeUrl(
                discovery.AuthorizationEndpoint,
                provider,
                BuildAbsoluteUri(context, AdminRedirectPath),
                state,
                challenge);

            return TypedResults.Ok(new AdminAuthAuthorizeUrlResponse
            {
                AuthorizeUrl = authorizeUrl
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to create authorize URL for provider {ProviderKey}.", provider.Key);
            return StandardErrorHelpers.CreateServiceUnavailable(context, "Identity provider is temporarily unavailable.");
        }
    }

    private static async Task<IResult> HandleRequestToken(
        string providerKey,
        AdminAuthTokenRequest request,
        HttpContext context,
        [FromServices] IOptions<OidcAuthenticationOptions> oidcOptions,
        [FromServices] AdminAuthSessionStore sessionStore,
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] ILogger<AdminAuthEndpointsLog> logger)
    {
        if (!OidcProviderCatalog.TryResolveProvider(oidcOptions.Value, providerKey, out var provider))
        {
            return StandardErrorHelpers.CreateNotFound(context, "Identity provider was not found.");
        }

        if (!TryValidateTokenRequest(request, out var validationError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError);
        }

        string? codeVerifier = null;
        string? pendingSessionId = null;
        if (string.Equals(request.GrantType, AuthorizationCodeGrantType, StringComparison.Ordinal))
        {
            pendingSessionId = context.Request.Cookies[AdminAuthSessionStore.PendingSessionCookieName];
            var pendingSession = await sessionStore.GetPendingSessionAsync(pendingSessionId, context.RequestAborted).ConfigureAwait(false);
            if (pendingSession is null ||
                !string.Equals(pendingSession.ProviderKey, provider.Key, StringComparison.Ordinal) ||
                !string.Equals(pendingSession.State, request.State, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(pendingSession.CodeVerifier))
            {
                DeleteCookie(context.Response, AdminAuthSessionStore.PendingSessionCookieName);
                return StandardErrorHelpers.CreateBadRequest(context, "Authorization session is invalid or expired.");
            }

            codeVerifier = pendingSession.CodeVerifier;
        }

        var baseUrlRequirement = RequireConfiguredPublicBaseUrl(context);
        if (baseUrlRequirement != null)
        {
            return baseUrlRequirement;
        }

        try
        {
            var discovery = await GetDiscoveryDocumentAsync(
                httpClientFactory,
                provider.Authority!,
                context.RequestAborted).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(discovery.TokenEndpoint))
            {
                logger.LogWarning("OIDC provider {ProviderKey} did not return a token endpoint.", provider.Key);
                return StandardErrorHelpers.CreateServiceUnavailable(context, "Identity provider is temporarily unavailable.");
            }

            using var response = await RequestTokenAsync(
                httpClientFactory,
                discovery.TokenEndpoint,
                provider,
                request,
                codeVerifier,
                BuildAbsoluteUri(context, AdminRedirectPath),
                context.RequestAborted).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "OIDC token request for provider {ProviderKey} failed with status code {StatusCode}.",
                    provider.Key,
                    (int)response.StatusCode);
                return StandardErrorHelpers.CreateBadRequest(context, "Authentication failed with the identity provider.");
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync(
                AdminAuthJsonContext.Default.AdminAuthTokenResponse,
                context.RequestAborted).ConfigureAwait(false);

            if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                logger.LogWarning("OIDC token request for provider {ProviderKey} returned an empty token response.", provider.Key);
                return StandardErrorHelpers.CreateServiceUnavailable(context, "Identity provider is temporarily unavailable.");
            }

            var validatedClaims = await ValidateAdminSessionIdTokenAsync(
                httpClientFactory,
                discovery,
                provider,
                tokenResponse.IdToken,
                context.RequestAborted).ConfigureAwait(false);
            if (validatedClaims is null ||
                !AdminAuthClaimsProjector.TryProjectValidatedClaims(validatedClaims, out var sessionClaims))
            {
                logger.LogWarning("OIDC token request for provider {ProviderKey} returned tokens that could not be projected into an admin session.", provider.Key);
                return StandardErrorHelpers.CreateServiceUnavailable(context, "Identity provider is temporarily unavailable.");
            }

            var expiresIn = tokenResponse.ExpiresIn > 0 ? tokenResponse.ExpiresIn : 300;
            var now = DateTimeOffset.UtcNow;
            var sessionExpiresAt = now.AddSeconds(expiresIn);

            // Clamp the session's effective lifetime to the earlier of the provider's
            // declared ExpiresIn and the ID token's own `exp` claim. Without this, a
            // session cookie can outlive the stored IdToken and every call that needs
            // the token after expiry fails silently on a different replica.
            var idTokenExpiry = TryGetIdTokenExpiry(validatedClaims);
            if (idTokenExpiry.HasValue && idTokenExpiry.Value < sessionExpiresAt)
            {
                sessionExpiresAt = idTokenExpiry.Value;
            }

            var cookieLifetime = sessionExpiresAt - now;
            if (cookieLifetime <= TimeSpan.Zero)
            {
                cookieLifetime = TimeSpan.FromSeconds(30);
                sessionExpiresAt = now + cookieLifetime;
            }

            var authSessionId = await sessionStore.CreateAuthenticatedSessionAsync(
                provider.Key,
                tokenResponse.AccessToken,
                tokenResponse.IdToken,
                sessionClaims,
                sessionExpiresAt,
                context.RequestAborted).ConfigureAwait(false);

            SetCookie(
                context.Response,
                AdminAuthSessionStore.AuthSessionCookieName,
                authSessionId,
                CreateAdminAuthCookieOptions(context, cookieLifetime));

            if (!string.IsNullOrWhiteSpace(pendingSessionId))
            {
                await sessionStore.RemovePendingSessionAsync(pendingSessionId, context.RequestAborted).ConfigureAwait(false);
            }

            DeleteCookie(context.Response, AdminAuthSessionStore.PendingSessionCookieName);

            return TypedResults.NoContent();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to request token for provider {ProviderKey}.", provider.Key);
            return StandardErrorHelpers.CreateServiceUnavailable(context, "Identity provider is temporarily unavailable.");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(pendingSessionId))
            {
                await sessionStore.RemovePendingSessionAsync(pendingSessionId, context.RequestAborted).ConfigureAwait(false);
            }

            DeleteCookie(context.Response, AdminAuthSessionStore.PendingSessionCookieName);
        }
    }

    private static async Task<IResult> HandleLogout(
        HttpContext context,
        [FromServices] AdminAuthSessionStore sessionStore,
        [FromServices] IOptions<OidcAuthenticationOptions> oidcOptions,
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] ILogger<AdminAuthEndpointsLog> logger)
    {
        var sessionId = context.Request.Cookies[AdminAuthSessionStore.AuthSessionCookieName];
        var session = await sessionStore.GetAuthenticatedSessionAsync(sessionId, context.RequestAborted).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            await sessionStore.RemoveAuthenticatedSessionAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
        }

        DeleteCookie(context.Response, AdminAuthSessionStore.AuthSessionCookieName);
        DeleteCookie(context.Response, AdminAuthSessionStore.PendingSessionCookieName);

        if (session is null || !OidcProviderCatalog.TryResolveProvider(oidcOptions.Value, session.ProviderKey, out var provider))
        {
            return TypedResults.Ok(new AdminAuthLogoutUrlResponse());
        }

        var baseUrlRequirement = RequireConfiguredPublicBaseUrl(context);
        if (baseUrlRequirement != null)
        {
            return baseUrlRequirement;
        }

        var logoutUrl = await TryBuildLogoutUrlAsync(
            provider,
            session.IdToken,
            context,
            httpClientFactory,
            logger).ConfigureAwait(false);

        return TypedResults.Ok(new AdminAuthLogoutUrlResponse
        {
            LogoutUrl = logoutUrl
        });
    }

    private static async Task<IResult> HandleGetLogoutUrl(
        string providerKey,
        string? idTokenHint,
        HttpContext context,
        [FromServices] IOptions<OidcAuthenticationOptions> oidcOptions,
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] ILogger<AdminAuthEndpointsLog> logger)
    {
        if (!OidcProviderCatalog.TryResolveProvider(oidcOptions.Value, providerKey, out var provider))
        {
            return StandardErrorHelpers.CreateNotFound(context, "Identity provider was not found.");
        }

        var baseUrlRequirement = RequireConfiguredPublicBaseUrl(context);
        if (baseUrlRequirement != null)
        {
            return baseUrlRequirement;
        }

        var logoutUrl = await TryBuildLogoutUrlAsync(
            provider,
            idTokenHint,
            context,
            httpClientFactory,
            logger).ConfigureAwait(false);

        return TypedResults.Ok(new AdminAuthLogoutUrlResponse
        {
            LogoutUrl = logoutUrl
        });
    }

    private static bool TryValidateTokenRequest(AdminAuthTokenRequest request, out string error)
    {
        var grantType = request.GrantType?.Trim();
        if (!string.Equals(grantType, AuthorizationCodeGrantType, StringComparison.Ordinal))
        {
            error = "Grant type is invalid.";
            return false;
        }

        if (string.Equals(grantType, AuthorizationCodeGrantType, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(request.Code))
            {
                error = "Authorization code is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.State))
            {
                error = "State is required.";
                return false;
            }

            if (request.State.Length > MaxStateLength)
            {
                error = "State is too long.";
                return false;
            }
        }

        error = string.Empty;
        return true;
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

    // Reads the standard JWT `exp` claim (seconds since Unix epoch) and returns the
    // corresponding UTC DateTimeOffset. Returns null when the claim is missing or
    // malformed — callers treat that as "no extra ceiling".
    private static DateTimeOffset? TryGetIdTokenExpiry(IReadOnlyList<Claim> claims)
    {
        foreach (var claim in claims)
        {
            if (!string.Equals(claim.Type, "exp", StringComparison.Ordinal))
            {
                continue;
            }

            if (long.TryParse(
                    claim.Value,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var epoch))
            {
                return DateTimeOffset.FromUnixTimeSeconds(epoch);
            }
        }

        return null;
    }

    private static async Task<IReadOnlyList<Claim>?> ValidateAdminSessionIdTokenAsync(
        IHttpClientFactory httpClientFactory,
        AdminAuthOidcDiscoveryDocument discovery,
        OidcProviderRegistration provider,
        string? idToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idToken) ||
            string.IsNullOrWhiteSpace(discovery.Issuer) ||
            string.IsNullOrWhiteSpace(discovery.JwksUri) ||
            string.IsNullOrWhiteSpace(provider.ClientId))
        {
            return null;
        }

        var handler = new JwtSecurityTokenHandler();
        if (!handler.CanReadToken(idToken))
        {
            return null;
        }

        var signingKeys = await GetOidcSigningKeysAsync(
            httpClientFactory,
            discovery.JwksUri,
            cancellationToken).ConfigureAwait(false);
        if (signingKeys.Count == 0)
        {
            return null;
        }

        try
        {
            var principal = handler.ValidateToken(
                idToken,
                new TokenValidationParameters
                {
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeys = signingKeys,
                    ValidateIssuer = true,
                    ValidIssuer = discovery.Issuer,
                    ValidateAudience = true,
                    ValidAudience = provider.ClientId,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(2)
                },
                out _);

            return principal.Claims.ToArray();
        }
        catch (SecurityTokenException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<SecurityKey>> GetOidcSigningKeysAsync(
        IHttpClientFactory httpClientFactory,
        string jwksUri,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(AdminAuthHttpClient);
        using var response = await client.GetAsync(jwksUri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var jwksJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(jwksJson))
        {
            return [];
        }

        var keySet = new JsonWebKeySet(jwksJson);
        return keySet.GetSigningKeys().ToArray();
    }

    private static async Task<HttpResponseMessage> RequestTokenAsync(
        IHttpClientFactory httpClientFactory,
        string tokenEndpoint,
        OidcProviderRegistration provider,
        AdminAuthTokenRequest request,
        string? codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var formValues = new Dictionary<string, string>
        {
            ["grant_type"] = request.GrantType!,
            ["client_id"] = provider.ClientId!
        };

        if (!string.IsNullOrWhiteSpace(provider.ClientSecret))
        {
            formValues["client_secret"] = provider.ClientSecret;
        }

        if (string.Equals(request.GrantType, AuthorizationCodeGrantType, StringComparison.Ordinal))
        {
            formValues["code"] = request.Code!;
            formValues["code_verifier"] = codeVerifier ?? throw new InvalidOperationException("Pending authorization session is missing the PKCE verifier.");
            formValues["redirect_uri"] = redirectUri;
        }

        var client = httpClientFactory.CreateClient(AdminAuthHttpClient);
        return await client.PostAsync(
            tokenEndpoint,
            new FormUrlEncodedContent(formValues),
            cancellationToken).ConfigureAwait(false);
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
            ["scope"] = string.Join(" ", provider.Scopes),
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };

        return QueryHelpers.AddQueryString(authorizationEndpoint, parameters);
    }

    private static string BuildAbsoluteUri(HttpContext context, string path)
    {
        return $"{BaseUrlResolver.GetBaseUrl(context)}{path}";
    }

    private static CookieOptions CreateAdminAuthCookieOptions(HttpContext context, TimeSpan lifetime)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            MaxAge = lifetime,
            Path = "/"
        };
    }

    private static void SetCookie(HttpResponse response, string name, string value, CookieOptions options)
    {
        response.Cookies.Append(name, value, options);
    }

    private static void DeleteCookie(HttpResponse response, string name)
    {
        response.Cookies.Delete(name, new CookieOptions
        {
            Path = "/"
        });
    }

    private static IResult? RequireConfiguredPublicBaseUrl(HttpContext context)
    {
        var environment = context.RequestServices.GetRequiredService<IHostEnvironment>();
        if (environment.IsDevelopment() || environment.IsEnvironment("Test"))
        {
            return null;
        }

        if (BaseUrlResolver.TryGetConfiguredBaseUrl(context, out _))
        {
            return null;
        }

        return StandardErrorHelpers.CreateServiceUnavailable(
            context,
            "Admin OIDC requires Public:BaseUrl to be configured outside development and test environments.");
    }

    private static async Task<string?> TryBuildLogoutUrlAsync(
        OidcProviderRegistration provider,
        string? idTokenHint,
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        ILogger<AdminAuthEndpointsLog> logger)
    {
        try
        {
            var discovery = await GetDiscoveryDocumentAsync(
                httpClientFactory,
                provider.Authority!,
                context.RequestAborted).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(discovery.EndSessionEndpoint))
            {
                return null;
            }

            var parameters = new Dictionary<string, string?>
            {
                ["post_logout_redirect_uri"] = BuildAbsoluteUri(context, AdminPostLogoutRedirectPath)
            };

            if (!string.IsNullOrWhiteSpace(idTokenHint))
            {
                parameters["id_token_hint"] = idTokenHint;
            }

            return QueryHelpers.AddQueryString(discovery.EndSessionEndpoint, parameters);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to create logout URL for provider {ProviderKey}.", provider.Key);
            return null;
        }
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
