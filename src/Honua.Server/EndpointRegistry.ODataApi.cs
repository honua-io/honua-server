// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> ODataEndpoints =>
    [
        new("GET", "/odata"),
        new("GET", "/odata/$metadata"),
        new("GET", "/odata/Layers"),
        new("GET", "/odata/Layers({layerId})"),
        new("GET", "/odata/Layers/$count"),
        new("GET", "/odata/Layers({layerId})/Features"),
        new("GET", "/odata/Layers({layerId})/Features/$count"),
        new("POST", "/odata/Layers({layerId})/Features"),
        new("GET", "/odata/Layers({layerId})/Features({objectId})"),
        new("PATCH", "/odata/Layers({layerId})/Features({objectId})"),
        new("PUT", "/odata/Layers({layerId})/Features({objectId})"),
        new("DELETE", "/odata/Layers({layerId})/Features({objectId})"),
        new("GET", "/odata/Features"),
        new("GET", "/odata/Features/$count"),
        new("POST", "/odata/Features"),
        new("GET", "/odata/Features(LayerId={layerId},ObjectId={objectId})"),
        new("GET", "/odata/Features(LayerId={layerId},ObjectId={objectId})/$ref"),
        new("GET", "/odata/Features(LayerId={layerId},ObjectId={objectId})/$value"),
        new("PATCH", "/odata/Features(LayerId={layerId},ObjectId={objectId})"),
        new("PUT", "/odata/Features(LayerId={layerId},ObjectId={objectId})"),
        new("DELETE", "/odata/Features(LayerId={layerId},ObjectId={objectId})"),
        new("GET", "/odata/Features({layerId})"),
        new("GET", "/odata/Features({layerId})/$count"),
        new("GET", "/odata/Features({layerId},{objectId})"),
        new("PATCH", "/odata/Features({layerId},{objectId})"),
        new("PUT", "/odata/Features({layerId},{objectId})"),
        new("DELETE", "/odata/Features({layerId},{objectId})"),
        new("POST", "/odata/$batch"),
        new("GET", "/odata/Features({layerId})/$apply"),
        new("GET", "/odata/Features({layerId})/$search"),
    ];
}
