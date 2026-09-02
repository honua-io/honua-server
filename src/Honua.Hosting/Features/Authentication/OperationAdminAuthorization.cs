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
    /// <summary>
    /// Canonical authorization result carried into the operation policy context.
    /// Adapters must use this value instead of asserting their own outcome.
    /// </summary>
    public readonly record struct Decision(bool IsAuthorized, string AuthorizationOutcome)
    {
        internal static Decision Allowed() => new(true, "authorized");

        internal static Decision Denied() => new(false, "denied");
    }

    /// <summary>Returns whether the caller may invoke the semantic operation.</summary>
    public static async Task<bool> IsAuthorizedAsync(
        HttpContext transportContext,
        ClaimsPrincipal principal,
        OperationSideEffectClass sideEffectClass,
        CancellationToken cancellationToken)
    {
        var decision = await EvaluateAsync(
            transportContext, principal, sideEffectClass, cancellationToken).ConfigureAwait(false);
        return decision.IsAuthorized;
    }

    /// <summary>
    /// Evaluates semantic admin authorization and returns the trusted outcome that
    /// may be propagated to the operation policy runtime.
    /// </summary>
    public static async Task<Decision> EvaluateAsync(
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
            return Decision.Denied();
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
        semanticContext.Request.Path = transportContext.Request.Path;

        var result = await authorization
            .AuthorizeAsync(principal, semanticContext, AuthenticationExtensions.AdminPolicy)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Decision.Denied();
        }

        // OIDC post-configuration broadens the shared Admin policy to configured
        // identity-provider roles. Keep persisted API-key grants as an independent
        // ceiling even in that mode; only stored keys carry api_key_id.
        var apiKeyAuthorized = principal.FindFirst("api_key_id") is null
            || AdminApiKeyPermission.IsAuthorized(
                principal,
                semanticContext.Request.Method,
                semanticContext.Request.Path.Value);
        return apiKeyAuthorized ? Decision.Allowed() : Decision.Denied();
    }
}
