// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Stac;

internal static class StacEndpoints
{
    internal sealed class StacEndpointsLog
    {
    }

    public static IEndpointRouteBuilder MapStacEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapStacCatalogEndpoints();
        endpoints.MapStacCollectionEndpoints();
        endpoints.MapStacItemEndpoints();
        endpoints.MapStacSearchEndpoints();

        return endpoints;
    }
}
