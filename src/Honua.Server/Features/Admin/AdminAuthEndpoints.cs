// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Anonymous admin auth bootstrap endpoints for the hosted admin UI.
/// Provider connection details stay server-side; the client receives only provider labels
/// and uses backend-assisted login, token exchange, refresh, and logout discovery.
/// </summary>
internal static class AdminAuthEndpoints
{
    private const string AdminAuthHttpClient = "AdminAuthOidc";
    private const string AuthorizationCodeGrantType = "authorization_code";
    private const string RefreshTokenGrantType = "refresh_token";
    private const string AdminRedirectPath = "/admin/auth/callback";
    private const string AdminPostLogoutRedirectPath = "/admin";
    private const int MaxStateLength = 512;
    private const int MaxPkceValueLength = 256;

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

        _ = group.MapPost("/providers/{providerKey}/authorize-url", HandleCreateAuthorizeUrl)
            .WithDisplayName("Create Admin Auth Authorize Url")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        _ = group.MapPost("/providers/{providerKey}/token", HandleRequestToken)
            .WithDisplayName("Request Admin Auth Token")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        _ = group.MapGet("/providers/{providerKey}/logout-url", HandleGetLogoutUrl)
            .WithDisplayName("Get Admin Auth Logout Url")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static Microsoft.AspNetCore.Http.HttpResults.Ok<AdminAuthConfigResponse> HandleGetAuthConfig(
        [FromServices] IOptions<OidcAuthenticationOptions> oidcOptions,
        [FromServices] ILogger<AdminAuthEndpointsLog> logger)
    {
        var providers = GetConfiguredProviders(oidcOptions.Value);
        AdminAuthLog.AuthConfigServed(logger, providers.Count);

        var response = new AdminAuthConfigResponse
        {
            OidcEnabled = oidcOptions.Value.Enabled && providers.Count > 0,
            Providers = providers
        };

        return TypedResults.Ok(response);
    }

    private static async Task<IResult> HandleCreateAuthorizeUrl(
        string providerKey,
        AdminAuthAuthorizeUrlRequest request,
        HttpContext context,
        [FromServices] IOptions<OidcAuthenticationOptions> oidcOptions,
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] ILogger<AdminAuthEndpointsLog> logger)
    {
        if (!TryValidateAuthorizeRequest(request, out var validationError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError);
        }

        if (!TryResolveProvider(oidcOptions.Value, providerKey, out var provider))
        {
            return StandardErrorHelpers.CreateNotFound(context, "Identity provider was not found.");
        }

        try
        {
            var discovery = await GetDiscoveryDocumentAsync(
                httpClientFactory,
                provider.Authority,
                context.RequestAborted).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(discovery.AuthorizationEndpoint))
            {
                logger.LogWarning("OIDC provider {ProviderKey} did not return an authorization endpoint.", provider.Key);
                return StandardErrorHelpers.CreateServiceUnavailable(context, "Identity provider is temporarily unavailable.");
            }

            var authorizeUrl = BuildAuthorizeUrl(
                discovery.AuthorizationEndpoint,
                provider,
                BuildAbsoluteUri(context, AdminRedirectPath),
                request.State!,
                request.CodeChallenge!);

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
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] ILogger<AdminAuthEndpointsLog> logger)
    {
        if (!TryValidateTokenRequest(request, out var validationError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError);
        }

        if (!TryResolveProvider(oidcOptions.Value, providerKey, out var provider))
        {
            return StandardErrorHelpers.CreateNotFound(context, "Identity provider was not found.");
        }

        try
        {
            var discovery = await GetDiscoveryDocumentAsync(
                httpClientFactory,
                provider.Authority,
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

            return TypedResults.Ok(tokenResponse);
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
    }

    private static async Task<IResult> HandleGetLogoutUrl(
        string providerKey,
        string? idTokenHint,
        HttpContext context,
        [FromServices] IOptions<OidcAuthenticationOptions> oidcOptions,
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] ILogger<AdminAuthEndpointsLog> logger)
    {
        if (!TryResolveProvider(oidcOptions.Value, providerKey, out var provider))
        {
            return StandardErrorHelpers.CreateNotFound(context, "Identity provider was not found.");
        }

        try
        {
            var discovery = await GetDiscoveryDocumentAsync(
                httpClientFactory,
                provider.Authority,
                context.RequestAborted).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(discovery.EndSessionEndpoint))
            {
                return TypedResults.Ok(new AdminAuthLogoutUrlResponse());
            }

            var parameters = new Dictionary<string, string?>
            {
                ["post_logout_redirect_uri"] = BuildAbsoluteUri(context, AdminPostLogoutRedirectPath)
            };

            if (!string.IsNullOrWhiteSpace(idTokenHint))
            {
                parameters["id_token_hint"] = idTokenHint;
            }

            return TypedResults.Ok(new AdminAuthLogoutUrlResponse
            {
                LogoutUrl = QueryHelpers.AddQueryString(discovery.EndSessionEndpoint, parameters)
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to create logout URL for provider {ProviderKey}.", provider.Key);
            return TypedResults.Ok(new AdminAuthLogoutUrlResponse());
        }
    }

    private static List<AdminAuthProviderInfo> GetConfiguredProviders(OidcAuthenticationOptions options)
    {
        var providers = new List<AdminAuthProviderInfo>();

        if (!options.Enabled)
        {
            return providers;
        }

        if (options.AzureAd?.IsValid == true)
        {
            providers.Add(new AdminAuthProviderInfo
            {
                Key = "azuread",
                DisplayName = "Microsoft Entra ID"
            });
        }

        if (options.Google?.IsValid == true)
        {
            providers.Add(new AdminAuthProviderInfo
            {
                Key = "google",
                DisplayName = "Google"
            });
        }

        if (options.Okta?.IsValid == true)
        {
            providers.Add(new AdminAuthProviderInfo
            {
                Key = "okta",
                DisplayName = "Okta"
            });
        }

        if (options.Auth0?.IsValid == true)
        {
            providers.Add(new AdminAuthProviderInfo
            {
                Key = "auth0",
                DisplayName = "Auth0"
            });
        }

        if (options.Generic?.IsValid == true)
        {
            providers.Add(new AdminAuthProviderInfo
            {
                Key = "oidc",
                DisplayName = options.Generic.DisplayName
            });
        }

        return providers;
    }

    private static bool TryResolveProvider(
        OidcAuthenticationOptions options,
        string providerKey,
        out AdminAuthProviderDefinition provider)
    {
        provider = default!;

        if (!options.Enabled || string.IsNullOrWhiteSpace(providerKey))
        {
            return false;
        }

        switch (providerKey.Trim().ToLowerInvariant())
        {
            case "azuread" when options.AzureAd?.IsValid == true:
                provider = new AdminAuthProviderDefinition(
                    "azuread",
                    $"{options.AzureAd.Instance}{options.AzureAd.TenantId}/v2.0",
                    options.AzureAd.ClientId!,
                    options.AzureAd.ClientSecret,
                    options.AzureAd.Scopes);
                return true;
            case "google" when options.Google?.IsValid == true:
                provider = new AdminAuthProviderDefinition(
                    "google",
                    "https://accounts.google.com",
                    options.Google.ClientId!,
                    options.Google.ClientSecret,
                    options.Google.Scopes);
                return true;
            case "okta" when options.Okta?.IsValid == true:
                provider = new AdminAuthProviderDefinition(
                    "okta",
                    options.Okta.GetAuthority(),
                    options.Okta.ClientId!,
                    options.Okta.ClientSecret,
                    options.Okta.Scopes);
                return true;
            case "auth0" when options.Auth0?.IsValid == true:
                provider = new AdminAuthProviderDefinition(
                    "auth0",
                    options.Auth0.GetAuthority(),
                    options.Auth0.ClientId!,
                    options.Auth0.ClientSecret,
                    options.Auth0.Scopes);
                return true;
            case "oidc" when options.Generic?.IsValid == true:
                provider = new AdminAuthProviderDefinition(
                    "oidc",
                    options.Generic.Authority!,
                    options.Generic.ClientId!,
                    options.Generic.ClientSecret,
                    options.Generic.Scopes);
                return true;
            default:
                return false;
        }
    }

    private static bool TryValidateAuthorizeRequest(AdminAuthAuthorizeUrlRequest request, out string error)
    {
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

        if (string.IsNullOrWhiteSpace(request.CodeChallenge))
        {
            error = "Code challenge is required.";
            return false;
        }

        if (request.CodeChallenge.Length > MaxPkceValueLength)
        {
            error = "Code challenge is too long.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateTokenRequest(AdminAuthTokenRequest request, out string error)
    {
        var grantType = request.GrantType?.Trim();
        if (!string.Equals(grantType, AuthorizationCodeGrantType, StringComparison.Ordinal) &&
            !string.Equals(grantType, RefreshTokenGrantType, StringComparison.Ordinal))
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

            if (string.IsNullOrWhiteSpace(request.CodeVerifier))
            {
                error = "Code verifier is required.";
                return false;
            }

            if (request.CodeVerifier.Length > MaxPkceValueLength)
            {
                error = "Code verifier is too long.";
                return false;
            }
        }

        if (string.Equals(grantType, RefreshTokenGrantType, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            error = "Refresh token is required.";
            return false;
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

    private static async Task<HttpResponseMessage> RequestTokenAsync(
        IHttpClientFactory httpClientFactory,
        string tokenEndpoint,
        AdminAuthProviderDefinition provider,
        AdminAuthTokenRequest request,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var formValues = new Dictionary<string, string>
        {
            ["grant_type"] = request.GrantType!,
            ["client_id"] = provider.ClientId
        };

        if (!string.IsNullOrWhiteSpace(provider.ClientSecret))
        {
            formValues["client_secret"] = provider.ClientSecret;
        }

        if (string.Equals(request.GrantType, AuthorizationCodeGrantType, StringComparison.Ordinal))
        {
            formValues["code"] = request.Code!;
            formValues["code_verifier"] = request.CodeVerifier!;
            formValues["redirect_uri"] = redirectUri;
        }
        else
        {
            formValues["refresh_token"] = request.RefreshToken!;
        }

        var client = httpClientFactory.CreateClient(AdminAuthHttpClient);
        return await client.PostAsync(
            tokenEndpoint,
            new FormUrlEncodedContent(formValues),
            cancellationToken).ConfigureAwait(false);
    }

    private static string BuildAuthorizeUrl(
        string authorizationEndpoint,
        AdminAuthProviderDefinition provider,
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

    private sealed record AdminAuthProviderDefinition(
        string Key,
        string Authority,
        string ClientId,
        string? ClientSecret,
        string[] Scopes);
}
