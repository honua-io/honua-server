// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Infrastructure.Middleware;

/// <summary>
/// Accepts the conventional ArcGIS application prefix before implicit endpoint routing.
/// </summary>
internal sealed class ArcGisPathBaseStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            // PathBase retains the prefix for generated links while Path uses the same
            // endpoints, authorization, tenancy, and rate limits as an unprefixed request.
            app.UsePathBase("/arcgis");
            next(app);
        };
    }
}
