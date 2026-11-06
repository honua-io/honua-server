// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Helpers;

namespace Honua.Infrastructure.Middleware;

internal sealed class GeoServicesFolderPathStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            app.UseGeoServicesFolderPaths();
            next(app);
        };
}
