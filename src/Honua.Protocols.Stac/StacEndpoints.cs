// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Stac;

/// <summary>
/// Extension methods to register STAC API endpoints.
/// </summary>
internal static class StacEndpoints
{
    /// <summary>
    /// Logging class for STAC endpoints.
    /// </summary>
    internal sealed class StacEndpointsLog
    {
    }

    /// <summary>
    /// Maps all STAC API endpoints by delegating to specialized endpoint classes.
    /// </summary>
    public static IEndpointRouteBuilder MapStacEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapStacCatalogEndpoints();
        endpoints.MapStacCollectionEndpoints();
        endpoints.MapStacItemEndpoints();
        endpoints.MapStacSearchEndpoints();

        return endpoints;
    }
}
