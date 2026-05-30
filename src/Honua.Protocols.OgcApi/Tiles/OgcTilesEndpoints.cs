// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Api.Tiles;

/// <summary>
/// Extension methods to register OGC API Tiles endpoints.
/// </summary>
/// <remarks>
/// CITE conformance: 16/16 (OGC API Tiles 1.0 `default` profile, 100% pass on trunk).
/// Authoritative status: <see href="../../../../../../docs/cite-status.md">docs/cite-status.md</see>.
/// </remarks>
internal static partial class OgcTilesEndpoints
{
    /// <summary>
    /// Maps all OGC API Tiles endpoints.
    /// </summary>
    /// <param name="endpoints">Route builder to register endpoints on.</param>
    /// <returns>The same route builder instance for chaining.</returns>
    public static IEndpointRouteBuilder MapOgcTilesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapCoreEndpoints();
        endpoints.MapCollectionsEndpoints();
        endpoints.MapTileMatrixSetEndpoints();
        endpoints.MapTilesEndpoints();

        return endpoints;
    }
}
