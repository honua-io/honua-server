// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Api.Tiles;

internal static partial class OgcTilesEndpoints
{
    public static IEndpointRouteBuilder MapOgcTilesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapCoreEndpoints();
        endpoints.MapCollectionsEndpoints();
        endpoints.MapTileMatrixSetEndpoints();
        endpoints.MapTilesEndpoints();

        return endpoints;
    }
}
