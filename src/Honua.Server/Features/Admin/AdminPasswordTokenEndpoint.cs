// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

internal sealed class AdminPasswordAuthenticator(
    ApiKeyAuthenticationDependencies dependencies,
    ILogger<AdminAuthEndpoints.AdminAuthEndpointsLog> logger)
{
    internal Task<ApiKeyValidation> ValidateAsync(string password, CancellationToken cancellationToken) =>
        ApiKeyAuthenticationHandler.ValidateAdminPasswordAsync(password,
            OidcAuthenticationExtensions.AdminSessionScheme, dependencies, logger, cancellationToken);

    internal static AdminAuthSessionClaim[] CreateClaims(ClaimsPrincipal principal) =>
        principal.Claims.Select(claim => new AdminAuthSessionClaim
        {
            Type = claim.Type,
            Value = claim.Value
        }).Append(new AdminAuthSessionClaim
        {
            Type = ClaimTypes.NameIdentifier,
            Value = ApiKeyAuthenticationHandler.BootstrapAdminActorId.ToString()
        }).ToArray();
}

internal sealed class AdminPasswordTokenEndpoint(
    AdminPasswordAuthenticator authenticator,
    OperatorBearerTokenService bearerTokenService,
    IConfiguration configuration)
{
    internal async Task<IResult> HandleAsync(HttpContext context, AdminPasswordSignInRequest request)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps
            || !CorsConfiguration.IsExplicitOriginAllowed(configuration, context.Request.Headers.Origin.ToString())
            || context.Request.Cookies.ContainsKey(AdminAuthSessionStore.AuthSessionCookieName)
            || string.IsNullOrEmpty(request.Password) || request.Password.Length > 1024)
        {
            return TypedResults.BadRequest();
        }

        var validation = await authenticator.ValidateAsync(request.Password, context.RequestAborted).ConfigureAwait(false);
        if (validation.Success?.Principal is not { } principal)
        {
            return validation.Rejection is ApiKeyRejection.AdminPasswordUnavailable or ApiKeyRejection.CredentialStoreUnavailable
                ? StandardErrorHelpers.CreateServiceUnavailable(context, "Administrator sign-in is unavailable.")
                : TypedResults.Unauthorized();
        }

        var issuance = bearerTokenService.Issue(AdminPasswordAuthenticator.CreateClaims(principal),
            DateTimeOffset.UtcNow.AddMinutes(30));
        if (issuance is null)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(context,
                "Operator bearer issuance is not configured on this server.");
        }

        return TypedResults.Ok(AdminAuthEndpoints.CreateOperatorBearerResponse(issuance));
    }
}
