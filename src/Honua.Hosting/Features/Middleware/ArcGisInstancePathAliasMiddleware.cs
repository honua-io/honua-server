// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Middleware;

/// <summary>
/// Serves the root-mounted surfaces under the <c>/arcgis</c> instance prefix that
/// ArcGIS clients assume, by stripping the prefix from the request path before routing.
/// </summary>
/// <remarks>
/// An ArcGIS Server is always reached as <c>https://host/&lt;instance&gt;/rest/services</c>,
/// and some Esri client code keys on that shape rather than on the documents it
/// gets back. <c>arcpy.geocoding.Locator(url)</c> is the case that established this:
/// against <c>https://host/rest/services/GeocodeServer</c> it fails with "Failed to
/// load locator" without sending a single request, against
/// <c>https://host/arcgis/rest/services/GeocodeServer</c> served by a transparent
/// path rewrite it loads, geocodes and reverse-geocodes, and a 308 redirect from the
/// aliased path is not enough - the client does not follow it. The same precheck
/// does not apply to FeatureServer, MapServer, SceneServer or the SOAP toolbox
/// path, which all resolve at the root.
/// <para>
/// This is an alias, not a second mount. The request's <see cref="HttpRequest.Path"/>
/// is rewritten in place and <see cref="HttpRequest.PathBase"/> is left alone, so
/// every route, policy and registry entry stays single and every advertised URL
/// keeps its canonical root-mounted form; a client that arrived through the alias
/// and follows an advertised link simply lands on the root path, which serves it
/// too. The whole <c>/arcgis</c> prefix is stripped, not just the three Esri roots:
/// ArcGIS clients also probe instance-relative paths beside the documented ones
/// (<c>/arcgis/</c>, <c>/arcgis/rest/static/...</c>), and the transparent proxy rewrite
/// that proved the Locator loads stripped the prefix unconditionally. What is not
/// served at the root is still a 404, so nothing new becomes reachable.
/// </para>
/// <para>
/// Placement is the whole point. <see cref="WebApplication"/> inserts its implicit
/// <c>UseRouting</c> ahead of every middleware registered in <c>Program.cs</c>, so a
/// rewrite registered there runs after endpoint matching has already failed on the
/// aliased path: the request then walks the rest of the pipeline with no endpoint,
/// the request logger records the status it saw, and the client receives the
/// not-found body. That is exactly how the first version of this alias behaved -
/// curl reported 200 and the body said 404, and the Locator did not load. Like
/// <see cref="HeadRequestStartupFilter"/>, the rewrite is therefore installed by an
/// <see cref="IStartupFilter"/>, the one seam that precedes the implicit routing
/// middleware. ArcGisInstancePathAliasTests pins this by reading the response body,
/// not the status.
/// </para>
/// </remarks>
internal sealed class ArcGisInstancePathAliasMiddleware
{
    private const string InstancePrefix = "/arcgis";

    private readonly RequestDelegate _next;

    public ArcGisInstancePathAliasMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(InstancePrefix, StringComparison.OrdinalIgnoreCase, out var remaining))
        {
            context.Request.Path = remaining.HasValue ? remaining : new PathString("/");
        }

        return _next(context);
    }
}

/// <summary>
/// Installs <see cref="ArcGisInstancePathAliasMiddleware"/> ahead of the implicit
/// <c>UseRouting</c> that <see cref="WebApplication"/> adds before any middleware
/// registered in <c>Program.cs</c>, so the rewritten path is what endpoint matching sees.
/// </summary>
internal sealed class ArcGisInstancePathAliasStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            app.UseMiddleware<ArcGisInstancePathAliasMiddleware>();
            next(app);
        };
    }
}

internal static class ArcGisInstancePathAliasExtensions
{
    /// <summary>
    /// Registers the pre-routing <c>/arcgis</c> alias. Must be called on the builder's
    /// services so the startup filter is in place before <see cref="WebApplication"/>
    /// builds its implicit routing middleware; registering the middleware on the built
    /// app would run it after matching and serve the not-found body under a 200.
    /// </summary>
    public static IServiceCollection AddHonuaArcGisInstancePathAlias(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IStartupFilter, ArcGisInstancePathAliasStartupFilter>();
        return services;
    }
}
