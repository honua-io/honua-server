// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Unauthenticated admin auth bootstrap endpoint.
/// Projects client-safe OIDC provider metadata so the Admin UI can configure its login flow.
/// </summary>
internal static class AdminAuthEndpoints
{
    /// <summary>
    /// Registers the admin auth configuration endpoint.
    /// This endpoint is intentionally anonymous so the Admin UI can bootstrap before authentication.
    /// </summary>
    public static void MapAdminAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/auth")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin Auth")
            .AllowAnonymous();

        _ = group.Map("/config", HandleGetAuthConfig)
            .WithDisplayName("Get Admin Auth Configuration")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    /// <summary>
    /// Handles GET /api/v1/admin/auth/config.
    /// Returns client-safe OIDC provider metadata. No secrets are exposed.
    /// </summary>
    private static async Task HandleGetAuthConfig(
        HttpContext context,
        IOptions<OidcAuthenticationOptions> oidcOptions,
        IWebHostEnvironment environment,
        ILoggerFactory loggerFactory)
    {
        var opts = oidcOptions.Value;
        var providers = new List<AdminAuthProviderInfo>();

        if (opts.Enabled)
        {
            if (opts.AzureAd?.IsValid == true)
            {
                providers.Add(BuildAzureAdProvider(opts.AzureAd));
            }

            if (opts.Google?.IsValid == true)
            {
                providers.Add(BuildGoogleProvider(opts.Google));
            }

            if (opts.Okta?.IsValid == true)
            {
                providers.Add(BuildOktaProvider(opts.Okta));
            }

            if (opts.Auth0?.IsValid == true)
            {
                providers.Add(BuildAuth0Provider(opts.Auth0));
            }

            if (opts.Generic?.IsValid == true)
            {
                providers.Add(BuildGenericProvider(opts.Generic));
            }
        }

        var response = new AdminAuthConfigResponse
        {
            OidcEnabled = opts.Enabled && providers.Count > 0,
            Providers = providers,
            ApiKeyFallbackEnabled = providers.Count == 0 && (environment.IsDevelopment() || !opts.Enabled)
        };

        var logger = loggerFactory.CreateLogger("Admin.Auth");
        AdminAuthLog.AuthConfigServed(logger, providers.Count);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            response,
            AdminAuthJsonContext.Default.AdminAuthConfigResponse,
            context.RequestAborted);
    }

    private static AdminAuthProviderInfo BuildAzureAdProvider(AzureAdProviderOptions azureAd)
    {
        return new AdminAuthProviderInfo
        {
            Key = "azuread",
            DisplayName = "Microsoft Entra ID",
            Authority = $"{azureAd.Instance}{azureAd.TenantId}/v2.0",
            ClientId = azureAd.ClientId!,
            Scopes = azureAd.Scopes,
            RedirectPath = "/admin/auth/callback",
            SupportsLogout = true,
            PostLogoutRedirectPath = "/admin"
        };
    }

    private static AdminAuthProviderInfo BuildGoogleProvider(GoogleProviderOptions google)
    {
        return new AdminAuthProviderInfo
        {
            Key = "google",
            DisplayName = "Google",
            Authority = "https://accounts.google.com",
            ClientId = google.ClientId!,
            Scopes = google.Scopes,
            RedirectPath = "/admin/auth/callback",
            // Google does not support RP-initiated logout via end_session_endpoint
            SupportsLogout = false,
            PostLogoutRedirectPath = null
        };
    }

    private static AdminAuthProviderInfo BuildOktaProvider(OktaProviderOptions okta)
    {
        return new AdminAuthProviderInfo
        {
            Key = "okta",
            DisplayName = "Okta",
            Authority = okta.GetAuthority(),
            ClientId = okta.ClientId!,
            Scopes = okta.Scopes,
            RedirectPath = "/admin/auth/callback",
            SupportsLogout = true,
            PostLogoutRedirectPath = "/admin"
        };
    }

    private static AdminAuthProviderInfo BuildAuth0Provider(Auth0ProviderOptions auth0)
    {
        return new AdminAuthProviderInfo
        {
            Key = "auth0",
            DisplayName = "Auth0",
            Authority = auth0.GetAuthority(),
            ClientId = auth0.ClientId!,
            Scopes = auth0.Scopes,
            RedirectPath = "/admin/auth/callback",
            SupportsLogout = !string.IsNullOrEmpty(auth0.SignedOutCallbackPath),
            PostLogoutRedirectPath = !string.IsNullOrEmpty(auth0.SignedOutCallbackPath)
                ? "/admin"
                : null
        };
    }

    private static AdminAuthProviderInfo BuildGenericProvider(GenericOidcProviderOptions generic)
    {
        return new AdminAuthProviderInfo
        {
            Key = "oidc",
            DisplayName = generic.DisplayName,
            Authority = generic.Authority!,
            ClientId = generic.ClientId!,
            Scopes = generic.Scopes,
            RedirectPath = "/admin/auth/callback",
            SupportsLogout = !string.IsNullOrEmpty(generic.SignedOutCallbackPath),
            PostLogoutRedirectPath = !string.IsNullOrEmpty(generic.SignedOutCallbackPath)
                ? "/admin"
                : null
        };
    }
}
