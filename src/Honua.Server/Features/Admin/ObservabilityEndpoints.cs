// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for lightweight observability support.
/// </summary>
internal static class ObservabilityEndpoints
{
    public static void MapAdminObservabilityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/observability")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Observability")
            .RequireAdminAuthorization();

        group.MapGet("/errors", HandleGetRecentErrors)
            .WithDisplayName("Get Recent Errors")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<RecentErrorsResponse>();

        group.MapGet("/telemetry", HandleGetTelemetryStatus)
            .WithDisplayName("Get Telemetry Status")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ObservabilityStatusResponse>();
    }

    private static IResult HandleGetRecentErrors([FromServices] RecentErrorBuffer buffer)
    {
        var response = new RecentErrorsResponse
        {
            Capacity = buffer.Capacity,
            Errors = buffer.Snapshot()
        };

        return Results.Json(response, MetricsJsonContext.Default.RecentErrorsResponse);
    }

    private static IResult HandleGetTelemetryStatus(
        [FromServices] IOptions<TracingOptions> options,
        [FromServices] IConfiguration configuration)
    {
        var tracingOptions = options.Value;
        var otlpEndpoint = ResolveOtlpEndpoint(tracingOptions, configuration);

        var response = new ObservabilityStatusResponse
        {
            TracingEnabled = tracingOptions.Enabled,
            OtlpConfigured = tracingOptions.Enabled && !string.IsNullOrWhiteSpace(otlpEndpoint),
            OtlpEndpoint = string.IsNullOrWhiteSpace(otlpEndpoint) ? null : otlpEndpoint
        };

        return Results.Json(response, MetricsJsonContext.Default.ObservabilityStatusResponse);
    }

    private static string? ResolveOtlpEndpoint(TracingOptions tracingOptions, IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(tracingOptions.OtlpEndpoint))
        {
            return tracingOptions.OtlpEndpoint;
        }

        return configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
    }
}
