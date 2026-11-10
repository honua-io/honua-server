// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Api.Processes;

/// <summary>
/// Extension methods to register OGC API Processes endpoints.
/// </summary>
internal static partial class OgcProcessesEndpoints
{
    /// <summary>
    /// Maps all OGC API Processes endpoints by delegating to specialized endpoint classes.
    /// </summary>
    public static IEndpointRouteBuilder MapOgcProcessesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapOgcProcessesCoreEndpoints();
        endpoints.MapOgcProcessesProcessEndpoints();
        endpoints.MapOgcProcessesJobEndpoints();

        return endpoints;
    }
}
