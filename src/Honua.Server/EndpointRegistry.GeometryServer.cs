// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> GeometryServerEndpoints =>
    [
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/buffer"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/buffer"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/simplify"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/simplify"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/project"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/project"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/intersect"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/intersect"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/union"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/union"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/clip"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/clip"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/difference"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/difference"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/areasAndLengths"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/areasAndLengths"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/lengths"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/lengths"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/distance"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/distance"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/relation"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/relation"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/densify"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/densify"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/convexHull"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/convexHull"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/generalize"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/generalize"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/labelPoints"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/labelPoints"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/cut"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/cut"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/trimExtend"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/trimExtend"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/offset"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/offset"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/autoComplete"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/autoComplete"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/reshape"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/reshape"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/findTransformations"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/findTransformations"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/toGeoCoordinateString"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/toGeoCoordinateString"),
        new("GET", "/rest/services/Utilities/Geometry/GeometryServer/fromGeoCoordinateString"),
        new("POST", "/rest/services/Utilities/Geometry/GeometryServer/fromGeoCoordinateString"),

    ];
}
