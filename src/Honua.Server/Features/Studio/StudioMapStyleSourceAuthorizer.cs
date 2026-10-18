// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Server.Features.Studio;

/// <summary>Shares the human style route's published-source authorization with MCP.</summary>
internal sealed class StudioMapStyleSourceAuthorizer(IHttpContextAccessor accessor) : IStudioMapStyleSourceAuthorizer
{
    public async Task<StudioCompositionBody> AuthorizeAsync(ClaimsPrincipal principal, StudioCompositionBody body, CancellationToken cancellationToken)
    {
        var authorizedContext = CreateCallerContext(principal);
        return await StudioDraftStyleInitializer.InitializeAsync(authorizedContext, body, cancellationToken).ConfigureAwait(false);
    }

    public Task<StudioCompositionBody> AuthorizeStyleReferencesAsync(ClaimsPrincipal principal,
        StudioCompositionBody body, CancellationToken cancellationToken)
        => StudioDraftStyleReferences.ResolveAsync(CreateCallerContext(principal), body, cancellationToken);

    private DefaultHttpContext CreateCallerContext(ClaimsPrincipal principal)
    {
        var context = accessor.HttpContext;
        if (context is null || principal.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException("A current authenticated style request is required.");
        // Native chat reaches MCP with its dedicated agent credential. Use the
        // authenticated caller and fresh request items for every access evaluation.
        var authorizedContext = new DefaultHttpContext
        {
            User = principal,
            RequestServices = context.RequestServices
        };
        authorizedContext.Request.PathBase = context.Request.PathBase;
        return authorizedContext;
    }
}
