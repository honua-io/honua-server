// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>
/// Extension methods to register WFS 2.0 endpoints
/// </summary>
internal static partial class Wfs20Endpoints
{
    /// <summary>
    /// Logging class for WFS 2.0 endpoints
    /// </summary>
    internal sealed class Wfs20EndpointsLog
    {
    }

    /// <summary>
    /// Maps all WFS 2.0 endpoints using a dispatcher pattern to handle routing
    /// </summary>
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
