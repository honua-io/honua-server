// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Api.Features;

internal static partial class OgcFeaturesEndpoints
{
    internal sealed class OgcFeaturesEndpointsLog
    {
    }

    public static IEndpointRouteBuilder MapOgcFeaturesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Register core metadata endpoints (landing page, conformance, OpenAPI)
        endpoints.MapCoreEndpoints();

        // Register collections management endpoints
        endpoints.MapCollectionsEndpoints();

        // Register features/items CRUD endpoints
        endpoints.MapFeaturesEndpoints();

        // Register H3 hexagonal grid aggregation endpoints
        endpoints.MapH3Endpoints();

        return endpoints;
    }

    // Note: The implementation methods have been moved to specialized classes:
    // - CoreEndpoints.cs - Landing page, conformance, OpenAPI specification
    // - CollectionsEndpoints.cs - Collections list, collection metadata, queryables
    // - FeaturesEndpoints.cs - Features CRUD operations
    // - OgcFeaturesUtilities.cs - Shared utilities and constants

    // The large utility methods and handlers that were previously in this file
    // are now organized into these focused files for better maintainability.

    // The logging methods and other supporting functionality remain here
    // for backwards compatibility with existing code.
}
