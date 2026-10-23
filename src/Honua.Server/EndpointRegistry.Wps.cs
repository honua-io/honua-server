// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> WpsEndpoints =>
    [
        // WPS 2.0.2 dispatcher plus the conformance result reference it emits.
        new("GET", "/wps"),
        new("POST", "/wps"),
        new("GET", "/wps/conformance/results/{token}"),
    ];
}
