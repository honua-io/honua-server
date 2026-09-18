// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Middleware;

/// <summary>
/// Serves the Esri surfaces under the <c>/arcgis</c> instance prefix that ArcGIS
/// clients assume, by rewriting <c>/arcgis/rest/...</c>, <c>/arcgis/services...</c>
/// and <c>/arcgis/sharing/...</c> to the canonical root-mounted paths before routing.
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
/// too. Only the three Esri roots are aliased: anything else under <c>/arcgis</c>
/// falls through to the normal 404.
/// </para>
/// </remarks>
internal sealed class ArcGisInstancePathAliasMiddleware
{
    private const string InstancePrefix = "/arcgis";

    private static readonly PathString[] AliasedRoots =
    [
        new("/rest"),
        new("/services"),
        new("/sharing"),
    ];

    private readonly RequestDelegate _next;

    public ArcGisInstancePathAliasMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(InstancePrefix, StringComparison.OrdinalIgnoreCase, out var remaining)
            && IsAliasedRoot(remaining))
        {
            context.Request.Path = remaining;
        }

        return _next(context);
    }

    private static bool IsAliasedRoot(PathString remaining)
    {
        foreach (var root in AliasedRoots)
        {
            if (remaining.StartsWithSegments(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

internal static class ArcGisInstancePathAliasExtensions
{
    /// <summary>
    /// Registers <see cref="ArcGisInstancePathAliasMiddleware"/>. Must run before routing.
    /// </summary>
    public static IApplicationBuilder UseArcGisInstancePathAlias(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ArcGisInstancePathAliasMiddleware>();
    }
}
