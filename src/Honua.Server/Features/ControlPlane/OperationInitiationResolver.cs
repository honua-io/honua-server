// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;

namespace Honua.ControlPlane;

internal sealed class OperationInitiationResolver(IHttpContextAccessor requests)
{
    public async Task<(OperationInitiationKind Kind, OperationInitiationChannel Channel)> ResolveAsync(
        CancellationToken cancellationToken)
    {
        var context = requests.HttpContext;
        if (context is null) return (OperationInitiationKind.Unknown, OperationInitiationChannel.Unknown);
        if (context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
            return (OperationInitiationKind.Agent, OperationInitiationChannel.Mcp);
        if (context.User.Identity?.IsAuthenticated != true)
            return (OperationInitiationKind.Unknown, OperationInitiationChannel.Unknown);

        var actor = CanonicalSecurityActor.Resolve(context.User);
        if (actor is not null
            && string.Equals(actor.AuthenticationScheme, OidcAuthenticationExtensions.AdminSessionScheme, StringComparison.OrdinalIgnoreCase)
            && !RequestCredentialCatalog.HasExplicitCredential(context.Request)
            && AdminSessionCrossSiteRequestGuard.HasTrustedOrigin(context.Request))
        {
            var sessions = context.RequestServices.GetService<AdminAuthSessionStore>();
            var session = sessions is null ? null : await sessions.GetInteractiveSessionForApprovalAsync(
                context.Request.Cookies[AdminAuthSessionStore.AuthSessionCookieName], cancellationToken).ConfigureAwait(false);
            if (session is not null)
            {
                var principal = AdminAuthClaimsProjector.CreatePrincipal(session.Claims, OidcAuthenticationExtensions.AdminSessionScheme);
                if (CanonicalSecurityActor.Resolve(principal)?.ActorId == actor.ActorId)
                    return (OperationInitiationKind.Human, OperationInitiationChannel.InteractiveOidc);
            }
        }

        return (OperationInitiationKind.Agent, OperationInitiationChannel.NoninteractiveHttp);
    }
}
