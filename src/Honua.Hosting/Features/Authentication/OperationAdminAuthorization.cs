// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Operations.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Applies the method-aware admin policy using an operation descriptor's semantic
/// side-effect class instead of the transport method used by HTTP or MCP.
/// </summary>
public static class OperationAdminAuthorization
{
    /// <summary>Returns whether the caller may invoke the semantic operation.</summary>
    public static async Task<bool> IsAuthorizedAsync(
        HttpContext transportContext,
        ClaimsPrincipal principal,
        OperationSideEffectClass sideEffectClass,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transportContext);
        ArgumentNullException.ThrowIfNull(principal);

        var authorization = transportContext.RequestServices.GetService<IAuthorizationService>();
        if (authorization is null)
        {
            return false;
        }

        var semanticContext = new DefaultHttpContext
        {
            RequestServices = transportContext.RequestServices,
            RequestAborted = cancellationToken,
            User = principal,
        };
        semanticContext.Request.Method = sideEffectClass == OperationSideEffectClass.ReadOnly
            ? HttpMethods.Get
            : HttpMethods.Post;

        var result = await authorization
            .AuthorizeAsync(principal, semanticContext, AuthenticationExtensions.AdminPolicy)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return false;
        }

        // OIDC post-configuration broadens the shared Admin policy to configured
        // identity-provider roles. Keep persisted API-key grants as an independent
        // ceiling even in that mode; only stored keys carry api_key_id.
        return principal.FindFirst("api_key_id") is null
            || AdminApiKeyPermission.IsAuthorized(principal, semanticContext.Request.Method);
    }
}
