// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> StaticMapEndpoints =>
    [
        // Static Map
        new("GET", "/static/{serviceId}/{center}/{dimensions}.{format}"),
        new("GET", "/static/{serviceId}/bbox/{bbox}/{dimensions}.{format}"),
    ];
}
