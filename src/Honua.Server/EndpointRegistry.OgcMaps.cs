// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> OgcMapsStylesEndpoints =>
    [
        new("GET", "/ogc/maps"),
        new("GET", "/ogc/maps/conformance"),
        new("GET", "/ogc/maps/openapi.json"),
        new("GET", "/ogc/maps/collections/{collectionId}/map"),
        new("GET", "/ogc/maps/collections/{collectionId}/styles/{styleId}/map"),
        new("GET", "/ogc/maps/collections/{collectionId}/map/tiles"),
        new("GET", "/ogc/maps/collections/{collectionId}/map/tiles/{tileMatrixSetId}"),
        new("GET", "/ogc/maps/map"),

        // OGC API - Styles (ADR-0048, Phase 1)
        new("GET", "/ogc/styles"),
        new("GET", "/ogc/styles/conformance"),
        new("GET", "/ogc/styles/openapi.json"),
        new("GET", "/ogc/styles/{styleId}"),
        new("GET", "/ogc/styles/{styleId}/metadata"),
        new("PUT", "/ogc/styles/{styleId}"),
        new("POST", "/ogc/styles"),
        new("DELETE", "/ogc/styles/{styleId}"),

    ];
}
