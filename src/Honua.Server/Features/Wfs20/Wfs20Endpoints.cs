// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Wfs20;

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
    public static IEndpointRouteBuilder MapWfs20Endpoints(this IEndpointRouteBuilder endpoints)
    {
        // Register single dispatcher endpoint that routes based on 'request' parameter
        // This prevents routing ambiguity issues when multiple operations map to the same path
        endpoints.MapWfs20DispatcherEndpoint();

        return endpoints;
    }

    // Note: The implementation methods are organized into specialized classes:
    // - Wfs20CoreEndpoints.cs - GetCapabilities operation
    // - Wfs20DescribeFeatureTypeEndpoints.cs - DescribeFeatureType operation
    // - Wfs20GetFeatureEndpoints.cs - GetFeature and GetPropertyValue operations
    // - Wfs20TransactionEndpoints.cs - Transaction operations (WFS-T)
    // - Wfs20Utilities.cs - Shared utilities and constants
    // - Models/Wfs20Models.cs - WFS 2.0 specific data models
}