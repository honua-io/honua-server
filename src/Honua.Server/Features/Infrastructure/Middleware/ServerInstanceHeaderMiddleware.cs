// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Hosting;
using Microsoft.Extensions.Primitives;

namespace Honua.Infrastructure.Middleware;

/// <summary>
/// Stamps every response with the <c>Honua-Instance</c> header carrying
/// <see cref="IServerInstanceIdentity.InstanceId"/>, so a caller behind a load balancer or Lambda
/// alias can tell which replica answered.
/// </summary>
/// <remarks>
/// The header is written from a <see cref="HttpResponse.OnStarting(Func{object, Task}, object)"/>
/// callback rather than up front: exception handlers and error envelopes that clear the response
/// (<c>HttpResponse.Clear()</c>) drop headers set earlier but keep starting callbacks, and a
/// response replayed from an output cache gets this replica's id rather than the one it was
/// captured on.
/// </remarks>
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

/// <summary>Extension methods for registering <see cref="ServerInstanceHeaderMiddleware"/>.</summary>
internal static class ServerInstanceHeaderMiddlewareExtensions
{
    /// <summary>
    /// Adds the <c>Honua-Instance</c> response header. Register first in the pipeline so responses
    /// short-circuited by later middleware (host validation, redirects, auth) carry it too.
    /// </summary>
    public static IApplicationBuilder UseServerInstanceHeader(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ServerInstanceHeaderMiddleware>();
    }
}
