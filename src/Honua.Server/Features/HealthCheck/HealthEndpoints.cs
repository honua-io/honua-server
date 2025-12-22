// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.HealthCheck;

/// <summary>
/// Health check endpoints with full AOT compatibility
/// </summary>
public static class HealthEndpoints
{
    /// <summary>
    /// Configure health endpoints using AOT-compatible routing
    /// </summary>
    public static void MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Use Map with explicit HTTP method to avoid MapGet reflection
        _ = endpoints.Map("/healthz/live", HandleLivenessProbe)
            .WithDisplayName("Liveness Probe")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = endpoints.Map("/healthz/ready", HandleReadinessProbe)
            .WithDisplayName("Readiness Probe")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    /// <summary>
    /// Handle liveness probe - indicates if the process is running
    /// </summary>
    private static async Task HandleLivenessProbe(HttpContext context)
    {
        // Ensure only GET requests
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = 405; // Method Not Allowed
            return;
        }

        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("Healthy");
    }

    /// <summary>
    /// Handle readiness probe - indicates if the service is ready to accept traffic
    /// Delegates health checking to dedicated service for better separation of concerns
    /// </summary>
    private static async Task HandleReadinessProbe(HttpContext context)
    {
        // Ensure only GET requests
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = 405; // Method Not Allowed
            return;
        }

        // Delegate health checking to dedicated service
        IReadinessCheckService readinessCheckService = context.RequestServices.GetRequiredService<IReadinessCheckService>();
        ReadinessResult result = await readinessCheckService.CheckReadinessAsync(context.RequestAborted);

        // Convert health check result to HTTP response
        await WriteHealthCheckResponse(context, result);
    }

    /// <summary>
    /// Writes health check result to HTTP response
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="result">Health check result</param>
    private static async Task WriteHealthCheckResponse(HttpContext context, ReadinessResult result)
    {
        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(result.Message);
    }
}

