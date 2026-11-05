// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Hosting;
using Microsoft.Extensions.Primitives;

namespace Honua.Infrastructure.Middleware;

// The header is applied in OnStarting so a cleared response keeps it and a cached response is stamped with this replica.
internal sealed class ServerInstanceHeaderMiddleware
{
    private readonly RequestDelegate _next;
    private readonly StringValues _headerValue;
    private readonly Func<object, Task> _applyHeader;

    public ServerInstanceHeaderMiddleware(RequestDelegate next, IServerInstanceIdentity identity)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        ArgumentNullException.ThrowIfNull(identity);
        _headerValue = new StringValues(identity.InstanceId);
        _applyHeader = ApplyHeader;
    }

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(_applyHeader, context);
        return _next(context);
    }

    private Task ApplyHeader(object state)
    {
        ((HttpContext)state).Response.Headers[ServerInstanceIdentity.HeaderName] = _headerValue;
        return Task.CompletedTask;
    }
}

internal static class ServerInstanceHeaderMiddlewareExtensions
{
    public static IApplicationBuilder UseServerInstanceHeader(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ServerInstanceHeaderMiddleware>();
    }
}
