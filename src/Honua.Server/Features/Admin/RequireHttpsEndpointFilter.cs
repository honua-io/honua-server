// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

internal sealed class RequireHttpsEndpointFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        context.Response.Headers.CacheControl = "no-store";
        // IsHttps reflects forwarded-header processing, so TLS-terminating proxies are honoured.
        return context.Request.IsHttps
            ? next(invocation)
            : ValueTask.FromResult<object?>(TypedResults.BadRequest());
    }
}
