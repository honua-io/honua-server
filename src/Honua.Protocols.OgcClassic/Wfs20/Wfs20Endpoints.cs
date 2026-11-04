// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Classic.Wfs20;

internal static partial class Wfs20Endpoints
{
    internal sealed class Wfs20EndpointsLog
    {
    }

    internal static IEndpointRouteBuilder MapWfs20Endpoints(this IEndpointRouteBuilder endpoints)
    {
        // Register single dispatcher endpoint that routes based on 'request' parameter
        // This prevents routing ambiguity issues when multiple operations map to the same path
        endpoints.MapWfs20DispatcherEndpoint();

        return endpoints;
    }

    // The runtime implementation is intentionally routed through the dispatcher/handler pair:
    // - Wfs20DispatcherEndpoint.cs - operation routing
    // - Services/Wfs20Handler.cs - request execution
    // - Wfs20Utilities.cs - shared utilities and constants
    // - Models/Wfs20Models.cs - XML contract types
}
