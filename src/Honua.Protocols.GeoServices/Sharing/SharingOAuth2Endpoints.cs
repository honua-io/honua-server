// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.Sharing;

/// <summary>
/// ArcGIS-compatible <c>/sharing/rest/oauth2</c> named-user endpoints (#1242):
/// <c>authorize</c>, <c>callback</c>, and <c>token</c>. Per ADR-0049 this is a thin
/// bridge over the shared OIDC identity core and <c>IPortalTokenIssuer</c> — it does
/// not introduce a parallel user or token store. The interactive
/// authorization-code+PKCE leg is delegated to the operator's configured OIDC
/// provider through <see cref="PortalOAuthBroker"/>; the access token returned to
/// the ArcGIS client is the same opaque portal token <c>generateToken</c> mints.
/// </summary>
internal static class SharingOAuth2Endpoints
{
    private const string JsonContentType = "application/json";

    /// <summary>Maps the OAuth2 authorize/callback/token endpoints.</summary>
    /// <param name="endpoints">Endpoint route builder to extend.</param>
    /// <returns>The original builder, to support fluent chaining.</returns>
    public static IEndpointRouteBuilder MapSharingOAuth2Endpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(PortalOAuthRoutes.AuthorizePath, HandleAuthorizeAsync)
            .WithDisplayName("ArcGIS Portal OAuth2 Authorize")
            .WithName("SharingRestOAuth2Authorize")
            .WithSummary("Initiate the ArcGIS named-user authorization-code flow")
            .WithDescription("Delegates identity to the configured OIDC provider (auth-code + PKCE) and returns an ArcGIS authorization code to the client redirect URI.")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces(StatusCodes.Status302Found)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        endpoints.MapGet(PortalOAuthRoutes.CallbackPath, HandleCallbackAsync)
            .WithDisplayName("ArcGIS Portal OAuth2 Callback")
            .WithName("SharingRestOAuth2Callback")
            .WithSummary("OIDC provider return endpoint for the ArcGIS named-user flow")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces(StatusCodes.Status302Found)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        endpoints.MapPost(PortalOAuthRoutes.TokenPath, HandleTokenAsync)
            .WithDisplayName("ArcGIS Portal OAuth2 Token")
            .WithName("SharingRestOAuth2TokenPost")
            .WithSummary("Exchange an authorization code or refresh token for a portal access token")
            .WithDescription("Returns the Esri-shaped { access_token, expires_in, refresh_token? } envelope; access_token is minted via IPortalTokenIssuer.")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces<OAuth2TokenResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces<OAuth2ErrorResponse>(StatusCodes.Status400BadRequest, JsonContentType)
            .Produces(StatusCodes.Status404NotFound);

        endpoints.MapGet(PortalOAuthRoutes.TokenPath, HandleTokenAsync)
            .WithDisplayName("ArcGIS Portal OAuth2 Token (GET)")
            .WithName("SharingRestOAuth2TokenGet")
            .WithSummary("Exchange an authorization code or refresh token via query parameters")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces<OAuth2TokenResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces<OAuth2ErrorResponse>(StatusCodes.Status400BadRequest, JsonContentType)
            .Produces(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> HandleAuthorizeAsync(
        HttpContext context,
        [FromServices] PortalOAuthBroker broker,
        [FromServices] IOptions<PortalTokenAuthenticationOptions> tokenOptions,
        [FromServices] ILogger<SharingRestLog> logger)
    {
        if (!tokenOptions.Value.Enabled)
        {
            return StandardErrorHelpers.CreateNotFound(context, "Portal OAuth2 is disabled.");
        }

        if (!broker.IsConfigured)
        {
            // No OIDC provider means there is no named-user identity to broker.
            return StandardErrorHelpers.CreateNotFound(context, "Portal OAuth2 named-user login is not configured.");
        }

        var query = context.Request.Query;
        var responseType = ReadFirst(query["response_type"]);
        if (responseType is not null && !string.Equals(responseType, "code", StringComparison.Ordinal))
        {
            return RedirectError(context, ReadFirst(query["redirect_uri"]), ReadFirst(query["state"]),
                "unsupported_response_type", "Only response_type=code is supported.");
        }

        var clientId = ReadFirst(query["client_id"]);
        var redirectUri = ReadFirst(query["redirect_uri"]);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(redirectUri))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "client_id and redirect_uri are required.");
        }

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out _))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "redirect_uri must be an absolute URI.");
        }

        // Open-redirect mitigation (#1484): the redirect_uri must be registered in
        // the per-deployment allow-list. A non-allow-listed redirect_uri is rejected
        // with a direct 400 and is NEVER redirected to (RFC 6749 §4.1.2.1) so the
        // bridge cannot be used to bounce an authorization code to a hostile host.
        if (!PortalOAuthRedirectUriValidator.IsAllowed(redirectUri, tokenOptions.Value.OAuth2.AllowedRedirectUris))
        {
            SharingRestLog.OAuthRedirectUriRejected(logger);
            return StandardErrorHelpers.CreateBadRequest(context, "redirect_uri is not registered for this deployment.");
        }

        // Hard PKCE requirement (#1484): when enabled, every authorization-code flow
        // must register a PKCE code_challenge up front so the matching code_verifier
        // is enforced at the token endpoint. Without this an intercepted code could
        // be redeemed by an attacker.
        if (tokenOptions.Value.OAuth2.RequirePkce && string.IsNullOrWhiteSpace(ReadFirst(query["code_challenge"])))
        {
            return RedirectError(context, redirectUri, ReadFirst(query["state"]),
                "invalid_request", "code_challenge is required (PKCE).");
        }

        var request = new PortalOAuthAuthorizeRequest(
            ClientId: clientId,
            RedirectUri: redirectUri,
            State: ReadFirst(query["state"]),
            CodeChallenge: ReadFirst(query["code_challenge"]),
            CodeChallengeMethod: ReadFirst(query["code_challenge_method"]),
            ProviderKey: ReadFirst(query["provider"]),
            ExpirationMinutes: ParseExpirationMinutes(ReadFirst(query["expiration"])));

        var idpAuthorizeUrl = await broker
            .BeginAuthorizeAsync(request, path => BuildAbsoluteUri(context, path), context.RequestAborted)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(idpAuthorizeUrl))
        {
            return RedirectError(context, redirectUri, request.State,
                "temporarily_unavailable", "Identity provider is unavailable.");
        }

        return Results.Redirect(idpAuthorizeUrl);
    }

    private static async Task<IResult> HandleCallbackAsync(
        HttpContext context,
        [FromServices] PortalOAuthBroker broker,
        [FromServices] IOptions<PortalTokenAuthenticationOptions> tokenOptions,
        [FromServices] ILogger<SharingRestLog> logger)
    {
        if (!tokenOptions.Value.Enabled || !broker.IsConfigured)
        {
            return StandardErrorHelpers.CreateNotFound(context, "Portal OAuth2 named-user login is not configured.");
        }

        var query = context.Request.Query;

        // Surface an IdP-side error verbatim shape but do not leak details.
        var idpError = ReadFirst(query["error"]);
        if (idpError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Authentication failed with the identity provider.");
        }

        var result = await broker
            .CompleteCallbackAsync(
                ReadFirst(query["state"]),
                ReadFirst(query["code"]),
                path => BuildAbsoluteUri(context, path),
                context.RequestAborted)
            .ConfigureAwait(false);

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.RedirectUri))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                result.ErrorDescription ?? "Authorization could not be completed.");
        }

        return Results.Redirect(result.RedirectUri);
    }

    private static async Task<IResult> HandleTokenAsync(
        HttpContext context,
        [FromServices] PortalOAuthTokenService tokenService,
        [FromServices] IOptions<PortalTokenAuthenticationOptions> tokenOptions,
        [FromServices] ILogger<SharingRestLog> logger)
    {
        if (!tokenOptions.Value.Enabled)
        {
            return StandardErrorHelpers.CreateNotFound(context, "Portal OAuth2 is disabled.");
        }

        var request = await ReadTokenRequestAsync(context).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(request.GrantType))
        {
            return OAuth2Error("invalid_request", "grant_type is required.");
        }

        var binding = ResolveBinding(context, request.RedirectUri);
        var result = await tokenService.ExchangeAsync(request, binding, context.RequestAborted).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return OAuth2Error(result.Error!, result.ErrorDescription!);
        }

        var response = new OAuth2TokenResponse
        {
            AccessToken = result.AccessToken!,
            ExpiresIn = result.ExpiresInSeconds,
            RefreshToken = result.RefreshToken,
        };

        return Results.Json(response, SharingRestJsonContext.Default.OAuth2TokenResponse, contentType: JsonContentType);
    }

    private static async Task<PortalOAuthTokenRequest> ReadTokenRequestAsync(HttpContext context)
    {
        string? grantType;
        string? code;
        string? codeVerifier;
        string? redirectUri;
        string? clientId;
        string? refreshToken;
        string? expiration;

        if (HttpMethods.IsPost(context.Request.Method) && context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            grantType = ReadFirst(form["grant_type"]);
            code = ReadFirst(form["code"]);
            codeVerifier = ReadFirst(form["code_verifier"]);
            redirectUri = ReadFirst(form["redirect_uri"]);
            clientId = ReadFirst(form["client_id"]);
            refreshToken = ReadFirst(form["refresh_token"]);
            expiration = ReadFirst(form["expiration"]);
        }
        else
        {
            var query = context.Request.Query;
            grantType = ReadFirst(query["grant_type"]);
            code = ReadFirst(query["code"]);
            codeVerifier = ReadFirst(query["code_verifier"]);
            redirectUri = ReadFirst(query["redirect_uri"]);
            clientId = ReadFirst(query["client_id"]);
            refreshToken = ReadFirst(query["refresh_token"]);
            expiration = ReadFirst(query["expiration"]);
        }

        // ArcGIS clients omit grant_type when sending a refresh_token; default
        // accordingly so the legacy refresh shape still works.
        if (string.IsNullOrWhiteSpace(grantType) && !string.IsNullOrWhiteSpace(refreshToken))
        {
            grantType = "refresh_token";
        }

        return new PortalOAuthTokenRequest(
            GrantType: grantType ?? string.Empty,
            Code: code,
            CodeVerifier: codeVerifier,
            RedirectUri: redirectUri,
            ClientId: clientId,
            RefreshToken: refreshToken,
            // ArcGIS Pro/Field Maps request a refresh token by sending the issuance
            // expiration on the authorization-code grant; we always offer one for the
            // authorization-code grant so long-lived clients work without a re-login.
            IncludeRefreshToken: true);
    }

    private static IResult OAuth2Error(string error, string description)
    {
        // OAuth2 (RFC 6749 §5.2) error responses are 400 with a JSON
        // { error, error_description } body.
        var response = new OAuth2ErrorResponse
        {
            Error = error,
            ErrorDescription = description,
        };
        return Results.Json(
            response,
            SharingRestJsonContext.Default.OAuth2ErrorResponse,
            contentType: JsonContentType,
            statusCode: StatusCodes.Status400BadRequest);
    }

    private static IResult RedirectError(HttpContext context, string? redirectUri, string? state, string error, string description)
    {
        // When a usable redirect_uri is present the OAuth2 spec requires the error
        // to be delivered there; otherwise fall back to a direct 400.
        if (!string.IsNullOrWhiteSpace(redirectUri) && Uri.TryCreate(redirectUri, UriKind.Absolute, out _))
        {
            var url = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
                redirectUri,
                new Dictionary<string, string?>
                {
                    ["error"] = error,
                    ["error_description"] = description,
                    ["state"] = state,
                });
            return Results.Redirect(url);
        }

        return StandardErrorHelpers.CreateBadRequest(context, description);
    }

    private static string ResolveBinding(HttpContext context, string? redirectUri)
    {
        // Bind the issued token to the redirect host (the ArcGIS client origin) when
        // available, falling back to the request referer, then the client IP-as-host
        // so the token still validates on the request path.
        if (!string.IsNullOrWhiteSpace(redirectUri))
        {
            return redirectUri;
        }

        var referer = context.Request.Headers.Referer.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(referer))
        {
            return referer;
        }

        return BaseUrlResolver.GetBaseUrl(context);
    }

    private static int? ParseExpirationMinutes(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            return parsed;
        }

        return null;
    }

    private static string BuildAbsoluteUri(HttpContext context, string path)
        => $"{BaseUrlResolver.GetBaseUrl(context).TrimEnd('/')}{path}";

    private static string? ReadFirst(StringValues values)
    {
        var value = values.FirstOrDefault();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
