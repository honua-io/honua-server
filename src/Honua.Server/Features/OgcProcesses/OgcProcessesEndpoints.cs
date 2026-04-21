// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.OgcProcesses;

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
