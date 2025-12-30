// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.OgcFeatures;

/// <summary>
/// Extension methods to register OGC API Features endpoints
/// </summary>
internal static partial class OgcFeaturesEndpoints
{
    /// <summary>
    /// Logging class for OGC Features endpoints
    /// </summary>
    internal sealed class OgcFeaturesEndpointsLog
    {
    }

    /// <summary>
    /// Maps all OGC API Features endpoints by delegating to specialized endpoint classes
    /// </summary>
    public static IEndpointRouteBuilder MapOgcFeaturesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Register core metadata endpoints (landing page, conformance, OpenAPI)
        endpoints.MapCoreEndpoints();

        // Register collections management endpoints
        endpoints.MapCollectionsEndpoints();

        // Register features/items CRUD endpoints
        endpoints.MapFeaturesEndpoints();

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
