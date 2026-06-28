// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> VectorTileServerEndpoints =>
    [
        new("GET", "/rest/services/{serviceId}/VectorTileServer"),
        new("POST", "/rest/services/{serviceId}/VectorTileServer"),
        new("GET", "/rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf"),
        new("GET", "/rest/services/{serviceId}/VectorTileServer/resources/styles"),
        new("GET", "/rest/services/{serviceId}/VectorTileServer/resources/styles/{**resourcePath}"),
        new("GET", "/rest/services/{serviceId}/VectorTileServer/resources/sprites/{spriteResource}"),
        new("GET", "/rest/services/{serviceId}/VectorTileServer/resources/fonts/{fontstack}/{range}.pbf"),
        new("GET", "/rest/services/{serviceId}/VectorTileServer/tilemap/{z}/{y}/{x}/{dimension}/{dimension2}"),
        new("GET", "/rest/services/{serviceId}/VectorTileServer/tilemap"),

    ];
}
