// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Core.Configuration;

// Limits:Query:MaxMapLayersPerRequest bounds how many layers or queries one map or feature
// request may name (WMS LAYERS, MapServer dynamicLayers, WFS queries).
public static class MapLayerRequestLimit
{
    public static int Resolve(IServiceProvider services)
    {
        var configured = services.GetService<IOptions<LimitsOptions>>()?.Value?.Query?.MaxMapLayersPerRequest;
        return configured is > 0 ? configured.Value : QueryLimits.DefaultMaxMapLayersPerRequest;
    }

    // A request may still name every layer its own service publishes.
    public static int ResolveForService(IServiceProvider services, int publishedLayerCount)
        => Math.Max(Resolve(services), publishedLayerCount);
}
