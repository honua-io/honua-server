// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Http.Json;
using Microsoft.AspNetCore.Antiforgery;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Configuration;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Server.Features.Admin;

// Provider connection details stay server-side; the anonymous client receives only provider labels.
internal static partial class AdminAuthEndpoints
{
    internal const string AdminAuthHttpClient = "AdminAuthOidc";
    private const string AuthorizationCodeGrantType = "authorization_code";
    private const string AdminRedirectPath = "/admin/auth/callback";
    private const string AdminPostLogoutRedirectPath = "/admin";
    private const int MaxStateLength = 512;
    private static readonly TimeSpan PendingSessionLifetime = TimeSpan.FromMinutes(15);

    // Restrict JWKS-validated ID tokens to asymmetric OIDC algorithms so "none" and HMAC
    // key-confusion attacks are rejected at validation time.
    private static readonly string[] AllowedIdTokenAlgorithms =
    [
        SecurityAlgorithms.RsaSha256,
        SecurityAlgorithms.RsaSha384,
        SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256,
        SecurityAlgorithms.RsaSsaPssSha384,
        SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256,
        SecurityAlgorithms.EcdsaSha384,
        SecurityAlgorithms.EcdsaSha512,
    ];

    internal sealed class AdminAuthEndpointsLog;

    public static void MapAdminAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        _ = endpoints.MapGet(AdminRedirectPath, (HttpContext context,
                AdminOidcCallbackEndpoint endpoint, IHttpClientFactory clients) => endpoint.HandleAsync(context, clients))
            .WithDisplayName("Complete Browser Administrator Sign In")
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
            .AllowAnonymous()
            .CacheOutput(static policy => policy.NoCache());

        _ = endpoints.MapGet(AdminPostLogoutRedirectPath, (HttpContext context, AdminOidcCallbackEndpoint endpoint)
                => endpoint.SignedOutLanding(context))
            .WithDisplayName("Land After Administrator Sign Out")
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
            .AllowAnonymous()
            .CacheOutput(static policy => policy.NoCache());

        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/auth")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin Auth")
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
            .AllowAnonymous()
            .CacheOutput(static policy => policy.NoCache());

        _ = group.MapGet("/config", HandleGetAuthConfig)
            .WithDisplayName("Get Admin Auth Configuration")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.MapGet("/session", HandleGetSession)
            .WithDisplayName("Get Admin Auth Session")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.MapGet("/csrf", HandleGetCsrf)
            .WithDisplayName("Get Browser Session CSRF Token");

        _ = group.MapPost("/session", (HttpContext context,
                AdminPasswordSignInRequest request, AdminPasswordSessionEndpoint endpoint) => endpoint.HandleAsync(context, request))
            .WithDisplayName("Sign In Browser Administrator Session")
            .AddEndpointFilter<RequireHttpsEndpointFilter>()
            .WithMetadata(new RequireBrowserCsrfMetadata())
            .WithMetadata(new RateLimitAttribute(5));

        _ = group.MapPost("/password-token", (HttpContext context,
                AdminPasswordSignInRequest request, AdminPasswordTokenEndpoint endpoint) => endpoint.HandleAsync(context, request))
            .WithDisplayName("Sign In Browser Administrator Bearer")
            .AddEndpointFilter<RequireHttpsEndpointFilter>()
            .WithMetadata(new RateLimitAttribute(5));

        _ = group.MapPost("/providers/{providerKey}/authorize-url", HandleCreateAuthorizeUrl)
            .WithDisplayName("Create Admin Auth Authorize Url")
            .WithMetadata(new UsesBrowserSessionMetadata())
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .WithMetadata(new RateLimitAttribute(5)); // 5 requests per minute for auth initiation

        _ = group.MapPost("/providers/{providerKey}/token", HandleRequestToken)
            .WithDisplayName("Request Admin Auth Token")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .WithMetadata(new RateLimitAttribute(5)); // 5 requests per minute for token exchange

        _ = group.MapPost("/bearer", HandleIssueOperatorBearer)
            .WithDisplayName("Issue Console Operator Bearer")
            .WithMetadata(new UsesBrowserSessionMetadata())
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .WithMetadata(new RateLimitAttribute(10)); // 10 requests per minute for bearer issuance

        _ = group.MapPost("/logout", HandleLogout)
            .WithDisplayName("Logout Admin Auth Session")
            .WithMetadata(new UsesBrowserSessionMetadata())
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        _ = group.MapGet("/providers/{providerKey}/logout-url", HandleGetLogoutUrl)
            .WithDisplayName("Get Admin Auth Logout Url")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static IResult HandleGetCsrf(HttpContext context, [FromServices] IAntiforgery antiforgery)
    {
        if (!context.Request.IsHttps)
        {
            return TypedResults.BadRequest();
        }

        context.Response.Headers.CacheControl = "no-store";
        var tokens = antiforgery.GetAndStoreTokens(context);
        return TypedResults.Ok(new AdminCsrfResponse { RequestToken = tokens.RequestToken! });
    }

    private static async Task<Microsoft.AspNetCore.Http.HttpResults.Ok<AdminAuthConfigResponse>> HandleGetAuthConfig(
        [FromServices] IOptions<OidcAuthenticationOptions> oidcOptions,
        [FromServices] IOptions<ClientCertificateAuthenticationOptions> clientCertificateOptions,
        [FromServices] IClientCertificateTrustStore clientCertificateTrustStore,
        [FromServices] ILogger<AdminAuthEndpointsLog> logger,
        CancellationToken cancellationToken)
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
            Providers = providers,
            ClientCertificates = await BuildClientCertificateInfoAsync(
                clientCertificateOptions.Value,
                clientCertificateTrustStore,
                cancellationToken).ConfigureAwait(false)
        };

        return TypedResults.Ok(response);
    }

    private static async Task<AdminAuthClientCertificateInfo> BuildClientCertificateInfoAsync(
        ClientCertificateAuthenticationOptions options,
        IClientCertificateTrustStore trustStore,
        CancellationToken cancellationToken)
    {
        // Pull from the live trust store so admin-API mutations (profile create/update/disable)
        // are reflected in the bootstrap hints. Configuration-only profiles are seeded into
        // the store at startup, so the store is the single source of truth for the active set.
        var profiles = await trustStore.ListProfilesAsync(options.EnvironmentId, cancellationToken)
            .ConfigureAwait(false);
        var enabledProfiles = profiles
            .Where(static profile => profile.Enabled)
            .ToArray();
        var mode = options.Mode.ToString();
        return new AdminAuthClientCertificateInfo
        {
            Mode = mode,
            EnvironmentId = options.EnvironmentId,
            RequiredForAdmin = options.Mode is ClientCertificateAuthenticationMode.RequiredForAdmin
                or ClientCertificateAuthenticationMode.RequiredForEnvironment,
            RequiredForNative = options.Mode is ClientCertificateAuthenticationMode.RequiredForNative
                or ClientCertificateAuthenticationMode.RequiredForEnvironment,
            SupportedTransports = options.Mode == ClientCertificateAuthenticationMode.Disabled
                ? []
                : ["https", "grpc-https"],
            AcceptedIssuerSubjects = enabledProfiles
                .SelectMany(static profile => profile.AcceptedIssuerSubjects)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            AcceptedIssuerThumbprints = enabledProfiles
                .SelectMany(static profile => profile.AcceptedIssuerThumbprints)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ExpirationWarningThresholdDays = enabledProfiles.Length == 0
                ? null
                : enabledProfiles.Min(static profile => profile.ExpirationWarningThresholdDays),
            ForwardedCertificateEnabled = options.ForwardedCertificate.Enabled,
        };
    }

    private static async Task<Microsoft.AspNetCore.Http.HttpResults.Ok<AdminAuthSessionResponse>> HandleGetSession(
        HttpContext context,
        [FromServices] AdminAuthSessionStore sessionStore)
    {
        if (context.User.Identity is { IsAuthenticated: true, AuthenticationType: OidcAuthenticationExtensions.OperatorBearerScheme })
        {
            return TypedResults.Ok(CreateSessionResponse(context.User, "operator-bearer", null));
        }

        if (context.User.Identity?.IsAuthenticated != true
            && (context.Request.Headers.ContainsKey("X-API-Key") || context.Request.Headers.ContainsKey("Authorization")))
        {
            return TypedResults.Ok(new AdminAuthSessionResponse());
        }

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

        return TypedResults.Ok(CreateSessionResponse(context.User, session.ProviderKey, session.ExpiresAt));
    }

    private static AdminAuthSessionResponse CreateSessionResponse(
        ClaimsPrincipal principal, string providerKey, DateTimeOffset? expiresAt) => new()
        {
            IsAuthenticated = true,
            ProviderKey = providerKey,
            ExpiresAt = expiresAt,
            Claims = principal.Claims.Select(claim => new AdminAuthClaimInfo
            {
                Type = claim.Type,
                Value = claim.Value
            }).ToList()
        };

    private static async Task<IResult> HandleIssueOperatorBearer(
        HttpContext context,
        [FromServices] AdminAuthSessionStore sessionStore,
        [FromServices] OperatorBearerTokenService bearerTokenService)
    {
        var (sessionId, session, unauthorized) = await ResolveAuthenticatedAdminSessionAsync(context, sessionStore)
            .ConfigureAwait(false);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        if (!bearerTokenService.Enabled)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(
                context,
                "Operator bearer issuance is not configured on this server.");
        }

        var binding = await sessionStore.BindOperatorBearerAsync(sessionId!, session!.ExpiresAt, context.RequestAborted)
            .ConfigureAwait(false);
        var issuance = bearerTokenService.Issue(session.Claims!, session.ExpiresAt, new OperatorBearerBinding(Session: binding));
        if (issuance is null)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(
                context,
                "Operator bearer issuance is not configured on this server.");
        }

        return TypedResults.Ok(CreateOperatorBearerResponse(issuance));
    }

    private static async Task<(string? SessionId, AdminAuthAuthenticatedSession? Session, IResult? Unauthorized)>
        ResolveAuthenticatedAdminSessionAsync(HttpContext context, AdminAuthSessionStore sessionStore)
    {
        var sessionId = context.Request.Cookies[AdminAuthSessionStore.AuthSessionCookieName];
        var session = await sessionStore.GetAuthenticatedSessionAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
        if (session is null || session.Claims is null || session.Claims.Length == 0)
        {
            DeleteCookie(context.Response, AdminAuthSessionStore.AuthSessionCookieName);
            return (null, null, StandardErrorHelpers.CreateUnauthorized(
                context,
                "An authenticated admin session is required to issue an operator bearer."));
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            await sessionStore.RemoveAuthenticatedSessionAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
            DeleteCookie(context.Response, AdminAuthSessionStore.AuthSessionCookieName);
            return (null, null, StandardErrorHelpers.CreateUnauthorized(
                context,
                "An authenticated admin session is required to issue an operator bearer."));
        }

        return (sessionId, session, null);
    }

    internal static AdminOperatorBearerResponse CreateOperatorBearerResponse(OperatorBearerIssuance issuance) =>
        new()
        {
            AccessToken = issuance.Token,
            TokenType = "Bearer",
            ExpiresAt = issuance.ExpiresAt,
            ExpiresIn = (long)Math.Max(0, (issuance.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds)
        };

    private static async Task<IResult> HandleCreateAuthorizeUrl(
        string providerKey,
        AdminAuthAuthorizeUrlRequest request,
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

        var returnTo = request.ReturnTo ?? AdminOidcCallbackEndpoint.DefaultReturnPath;
        if (!AdminOidcCallbackEndpoint.IsAllowedReturnPath(returnTo))
        {
            return TypedResults.BadRequest();
        }

        var baseUrlRequirement = RequireConfiguredAdminCallback(context, oidcOptions.Value);
        if (baseUrlRequirement != null)
        {
            return baseUrlRequirement;
        }

        try
        {
            var discoveryPolicy = BuildAuthorityDiscoveryPolicy(context, oidcOptions.Value.RequireHttps);
            var discovery = await GetDiscoveryDocumentAsync(
                httpClientFactory,
                new AdminDiscoveryFetchRequest(provider.Authority!, discoveryPolicy),
                context.RequestAborted).ConfigureAwait(false);

            var isValidAuthorizeEndpoint = oidcOptions.Value.RequireHttps
                ? PortalOAuthBroker.IsValidHttpsBrowserRedirectEndpoint(discovery.AuthorizationEndpoint)
                : PortalOAuthBroker.IsValidHttpOrHttpsBrowserRedirectEndpoint(discovery.AuthorizationEndpoint);
            if (!isValidAuthorizeEndpoint)
            {
                AdminAuthLog.MissingAuthorizationEndpoint(logger, provider.Key);
                return StandardErrorHelpers.CreateServiceUnavailable(
                    context,
                    "Identity provider is temporarily unavailable.",
                    retryable: true);
            }

            var state = GenerateRandomString(32);
            var nonce = GenerateRandomString(32);
            var verifier = GenerateRandomString(64);
            var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var pendingSessionId = await sessionStore.CreatePendingSessionAsync(
                new AdminAuthPendingSession
                {
                    ProviderKey = provider.Key,
                    State = state,
                    Nonce = nonce,
                    CodeVerifier = verifier,
                    ExpiresAt = DateTimeOffset.UtcNow.Add(PendingSessionLifetime),
                    ReturnTo = returnTo
                }, context.RequestAborted).ConfigureAwait(false);

            if (context.Request.Cookies.TryGetValue(AdminAuthSessionStore.AuthSessionCookieName, out var existingSessionId))
            {
                await sessionStore.RemoveAuthenticatedSessionAsync(existingSessionId, context.RequestAborted).ConfigureAwait(false);
                DeleteCookie(context.Response, AdminAuthSessionStore.AuthSessionCookieName);
            }

            // The provider returns through a top-level cross-site GET.
            var pendingCookieOptions = CreateAdminAuthCookieOptions(context, PendingSessionLifetime);
            pendingCookieOptions.SameSite = SameSiteMode.Lax;
            SetCookie(context.Response, AdminAuthSessionStore.PendingSessionCookieName, pendingSessionId, pendingCookieOptions);

            var authorizeUrl = BuildAuthorizeUrl(new AdminAuthorizeUrlParameters
            {
                AuthorizationEndpoint = discovery.AuthorizationEndpoint,
                Provider = provider,
                RedirectUri = ResolveAdminCallbackUri(context, oidcOptions.Value),
                State = state,
                Nonce = nonce,
                CodeChallenge = challenge
            });

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
            AdminAuthLog.CreateAuthorizeUrlFailed(logger, provider.Key, ex);
            return StandardErrorHelpers.CreateServiceUnavailable(
                context,
                "Identity provider is temporarily unavailable.",
                retryable: true);
        }
    }

    internal static async Task<IResult> HandleRequestToken(
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

        var pendingSessionId = context.Request.Cookies[AdminAuthSessionStore.PendingSessionCookieName];
        var pendingSession = await sessionStore.GetPendingSessionAsync(pendingSessionId, context.RequestAborted).ConfigureAwait(false);
        if (pendingSession is null ||
            !string.Equals(pendingSession.ProviderKey, provider.Key, StringComparison.Ordinal) ||
            !PortalOAuthBroker.CryptographicEquals(pendingSession.State, request.State) ||
            string.IsNullOrWhiteSpace(pendingSession.CodeVerifier))
        {
            DeleteCookie(context.Response, AdminAuthSessionStore.PendingSessionCookieName);
            return StandardErrorHelpers.CreateBadRequest(context, "Authorization session is invalid or expired.");
        }

        var codeVerifier = pendingSession.CodeVerifier;
        var networkPolicy = ResolveDiscoveryNetworkPolicy(context);

        var baseUrlRequirement = RequireConfiguredAdminCallback(context, oidcOptions.Value);
        if (baseUrlRequirement != null)
        {
            return baseUrlRequirement;
        }

        try
        {
            var discoveryPolicy = BuildAuthorityDiscoveryPolicy(context, oidcOptions.Value.RequireHttps);
            var discovery = await GetDiscoveryDocumentAsync(
                httpClientFactory,
                new AdminDiscoveryFetchRequest(provider.Authority!, discoveryPolicy),
                context.RequestAborted).ConfigureAwait(false);

            var tokenPolicy = BuildDiscoveryEndpointPolicy(
                discovery.TokenEndpoint,
                provider,
                new AdminDiscoveryPolicyOptions(oidcOptions.Value, networkPolicy));
            if (!await PortalOAuthBroker.IsValidDiscoveryEndpointAsync(
                    discovery.TokenEndpoint,
                    tokenPolicy,
                    context.RequestAborted).ConfigureAwait(false))
            {
                AdminAuthLog.MissingTokenEndpoint(logger, provider.Key);
                return StandardErrorHelpers.CreateServiceUnavailable(
                    context,
                    "Identity provider is temporarily unavailable.",
                    retryable: true);
            }

            using var response = await RequestTokenAsync(
                new AdminTokenExchangeContext
                {
                    HttpClientFactory = httpClientFactory,
                    TokenEndpoint = discovery.TokenEndpoint,
                    Provider = provider,
                    Request = request,
                    CodeVerifier = codeVerifier,
                    RedirectUri = ResolveAdminCallbackUri(context, oidcOptions.Value)
                },
                context.RequestAborted).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                AdminAuthLog.TokenRequestFailed(logger, provider.Key, (int)response.StatusCode);
                return StandardErrorHelpers.CreateBadRequest(context, "Authentication failed with the identity provider.");
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync(
                AdminAuthJsonContext.Default.AdminAuthTokenResponse,
                context.RequestAborted).ConfigureAwait(false);

            if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                AdminAuthLog.EmptyTokenResponse(logger, provider.Key);
                return StandardErrorHelpers.CreateServiceUnavailable(
                    context,
                    "Identity provider is temporarily unavailable.",
                    retryable: true);
            }

            var validatedClaims = await ValidateAdminSessionIdTokenAsync(
                new AdminIdTokenValidationContext
                {
                    HttpClientFactory = httpClientFactory,
                    Discovery = discovery,
                    Provider = provider,
                    OidcOptions = oidcOptions.Value,
                    IdToken = tokenResponse.IdToken,
                    ExpectedNonce = pendingSession.Nonce,
                    NetworkPolicy = networkPolicy
                },
                context.RequestAborted).ConfigureAwait(false);
            if (validatedClaims is null ||
                !AdminAuthClaimsProjector.TryProjectValidatedClaims(validatedClaims, out var sessionClaims))
            {
                AdminAuthLog.InvalidTokenProjection(logger, provider.Key);
                return StandardErrorHelpers.CreateServiceUnavailable(
                    context,
                    "Identity provider is temporarily unavailable.",
                    retryable: true);
            }

            var persistResult = await PersistInteractiveOidcSessionAsync(
                context,
                sessionStore,
                new AdminTokenSessionInput
                {
                    Provider = provider,
                    TokenResponse = tokenResponse,
                    ValidatedClaims = validatedClaims,
                    SessionClaims = sessionClaims,
                    PendingSessionId = pendingSessionId
                }).ConfigureAwait(false);
            if (persistResult is not Microsoft.AspNetCore.Http.HttpResults.NoContent)
            {
                AdminAuthLog.InvalidTokenProjection(logger, provider.Key);
            }

            return persistResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            AdminAuthLog.RequestTokenFailed(logger, provider.Key, ex);
            return StandardErrorHelpers.CreateServiceUnavailable(
                context,
                "Identity provider is temporarily unavailable.",
                retryable: true);
        }
        finally
        {
            await CleanupPendingSessionAsync(context, sessionStore, pendingSessionId).ConfigureAwait(false);
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
            try
            {
                await sessionStore.RemoveAuthenticatedSessionAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
            }
            catch (ServiceUnavailableException)
            {
                return StandardErrorHelpers.CreateServiceUnavailable(
                    context,
                    "Session could not be terminated. Retry logout when the session store is available.",
                    retryable: true);
            }
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

        var logoutUrl = await TryBuildLogoutUrlAsync(new AdminLogoutUrlContext
        {
            Provider = provider,
            IdTokenHint = session.IdToken,
            HttpsPolicy = ResolveRedirectHttpsPolicy(oidcOptions.Value),
            HttpContext = context,
            HttpClientFactory = httpClientFactory,
            Logger = logger
        }).ConfigureAwait(false);

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

        var logoutUrl = await TryBuildLogoutUrlAsync(new AdminLogoutUrlContext
        {
            Provider = provider,
            IdTokenHint = idTokenHint,
            HttpsPolicy = ResolveRedirectHttpsPolicy(oidcOptions.Value),
            HttpContext = context,
            HttpClientFactory = httpClientFactory,
            Logger = logger
        }).ConfigureAwait(false);

        return TypedResults.Ok(new AdminAuthLogoutUrlResponse
        {
            LogoutUrl = logoutUrl
        });
    }

    private static bool TryValidateTokenRequest(AdminAuthTokenRequest request, out string error)
    {
        if (!string.Equals(request.GrantType, AuthorizationCodeGrantType, StringComparison.Ordinal))
        {
            error = "Grant type is invalid.";
            return false;
        }

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

        error = string.Empty;
        return true;
    }

    private static string BuildAbsoluteUri(HttpContext context, string path)
    {
        return $"{BaseUrlResolver.GetBaseUrl(context)}{path}";
    }

    internal static CookieOptions CreateAdminAuthCookieOptions(HttpContext context, TimeSpan lifetime)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Strict,
            Secure = UseSecureSessionCookie(context),
            MaxAge = lifetime,
            Path = "/"
        };
    }

    // Behind a TLS-terminating proxy whose forwarded headers are not trusted the request looks
    // like HTTP; a configured https public base URL still keeps the session cookie off plain
    // HTTP. Outside Development and Test the cookie is always Secure: the CSRF, password and OIDC
    // callback endpoints refuse plain HTTP everywhere, so only SAML sign-in changes (it needs HTTPS
    // at the browser there).
    internal static bool UseSecureSessionCookie(HttpContext context)
        => context.Request.IsHttps
           || (context.RequestServices.GetService<IHostEnvironment>() is { } environment
               && !environment.IsDevelopment() && !environment.IsEnvironment("Test"))
           || BaseUrlResolver.GetBaseUrl(context).StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static void SetCookie(HttpResponse response, string name, string value, CookieOptions options)
    {
        response.Cookies.Append(name, value, options);
    }

    internal static void DeleteCookie(HttpResponse response, string name)
    {
        response.Cookies.Delete(name, new CookieOptions
        {
            Path = "/"
        });
    }

    private static string ResolveAdminCallbackUri(HttpContext context, OidcAuthenticationOptions options) =>
        options.AdminAuthCallbackUri ?? BuildAbsoluteUri(context, AdminRedirectPath);

    private static IResult? RequireConfiguredAdminCallback(HttpContext context, OidcAuthenticationOptions options)
    {
        if (!options.TryGetAdminAuthCallbackUri(out var callback))
        {
            return StandardErrorHelpers.CreateServiceUnavailable(context,
                "Admin OIDC callback configuration is invalid.");
        }

        return callback is not null ? null : RequireConfiguredPublicBaseUrl(context);
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
}
