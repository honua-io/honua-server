// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Endpoints;

/// <summary>
/// Health check endpoints with AOT compatibility
/// </summary>
public static class HealthEndpoints
{
    /// <summary>
    /// Configure health endpoints
    /// </summary>
    public static void MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/healthz/live", GetLiveness)
            .WithName("GetLiveness")
            .WithDisplayName("Liveness Probe");

        endpoints.MapGet("/healthz/ready", GetReadiness)
            .WithName("GetReadiness")
            .WithDisplayName("Readiness Probe");
    }

    /// <summary>
    /// Liveness probe - indicates if the process is running
    /// </summary>
    private static IResult GetLiveness() => Results.Ok("Healthy");

    /// <summary>
    /// Readiness probe - indicates if the service is ready to accept traffic
    /// </summary>
    private static IResult GetReadiness() => Results.Ok("Ready");
}
