// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

/// <summary>
/// Refuses credential-bearing sign-in requests that did not arrive over HTTPS (after forwarded-header
/// processing), so passwords are never accepted in clear text. The response is never cached.
/// </summary>
internal sealed class RequireHttpsEndpointFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        context.Response.Headers.CacheControl = "no-store";
        return context.Request.IsHttps
            ? next(invocation)
            : ValueTask.FromResult<object?>(TypedResults.BadRequest());
    }
}
