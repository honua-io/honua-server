// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

internal sealed class AdminPasswordSessionEndpoint(
    AdminPasswordAuthenticator authenticator,
    AdminAuthSessionStore sessionStore,
    ILogger<AdminAuthEndpoints.AdminAuthEndpointsLog> logger)
{
    internal async Task<IResult> HandleAsync(HttpContext context, AdminPasswordSignInRequest request)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length > 1024)
        {
            return TypedResults.BadRequest();
        }

        var validation = await authenticator.ValidateAsync(context, request.Password).ConfigureAwait(false);
        if (validation.Success?.Principal is not { } principal)
        {
            return AdminPasswordAuthenticator.Reject(context, validation);
        }

        try
        {
            return await CreateSessionAsync(context, principal).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // Cache providers surface heterogeneous failures; never issue a local-only session.
            AdminAuthLog.PasswordSessionCreationFailed(logger, ex);
            return StandardErrorHelpers.CreateServiceUnavailable(context, "Administrator sign-in is unavailable.");
        }
    }

    private async Task<IResult> CreateSessionAsync(HttpContext context, System.Security.Claims.ClaimsPrincipal principal)
    {
        var lifetime = TimeSpan.FromMinutes(30);
        await sessionStore.RemoveAuthenticatedSessionAsync(
            context.Request.Cookies[AdminAuthSessionStore.AuthSessionCookieName], context.RequestAborted)
            .ConfigureAwait(false);
        var sessionId = await sessionStore.CreateAuthenticatedSessionAsync(new AdminAuthAuthenticatedSession
        {
            Origin = AdminAuthSessionOrigin.Builtin,
            ProviderKey = "builtin",
            AccessToken = string.Empty,
            Claims = AdminPasswordAuthenticator.CreateClaims(principal),
            ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime)
        }, context.RequestAborted).ConfigureAwait(false);
        context.Response.Cookies.Append(AdminAuthSessionStore.AuthSessionCookieName, sessionId,
            AdminAuthEndpoints.CreateAdminAuthCookieOptions(context, lifetime));
        return TypedResults.NoContent();
    }
}
