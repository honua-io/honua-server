// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.CloudDemo;

internal static class CloudDemoEndpoints
{
    private const string WritableServiceId = "field-inspections-demo-writable";
    private const int WritableContractLayerId = 0;
    private const string DefaultIncidentSourceId = "incident-ops";
    private const string DefaultIncidentLayerId = "incident-points";

    public static void MapCloudDemoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/v1/cloud-demo/reset", HandleResetAsync)
            .WithDisplayName("Reset Cloud Demo Writable Service")
            .WithTags("CloudDemo")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .AllowAnonymous()
            .Produces<CloudDemoResetResponse>(StatusCodes.Status200OK, "application/json")
            .Produces<CloudDemoProblemResponse>(StatusCodes.Status401Unauthorized, "application/json")
            .Produces<CloudDemoProblemResponse>(StatusCodes.Status403Forbidden, "application/json")
            .Produces<CloudDemoProblemResponse>(StatusCodes.Status503ServiceUnavailable, "application/json");

        endpoints.MapGet("/api/v1/realtime/incidents/sse", HandleIncidentSseAsync)
            .WithDisplayName("Stream Cloud Demo Incidents")
            .WithTags("CloudDemo", "Streaming")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .AllowAnonymous()
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces<CloudDemoProblemResponse>(StatusCodes.Status404NotFound, "application/json");
    }

    private static async Task<IResult> HandleResetAsync(
        HttpContext context,
        [FromServices] IConfiguration configuration,
        [FromServices] CloudDemoServiceSeeder seeder)
    {
        AddNoStoreHeaders(context.Response);

        if (!CloudDemoConfiguration.IsEnabled(configuration))
        {
            return JsonProblem(
                StatusCodes.Status404NotFound,
                "cloud_demo_disabled",
                "Cloud demo services are not enabled.");
        }

        if (!CloudDemoConfiguration.AllowWrites(configuration))
        {
            return JsonProblem(
                StatusCodes.Status403Forbidden,
                "cloud_demo_writes_disabled",
                "Cloud demo writes are disabled.");
        }

        var resetToken = CloudDemoConfiguration.ResetToken(configuration);
        if (string.IsNullOrWhiteSpace(resetToken))
        {
            return JsonProblem(
                StatusCodes.Status503ServiceUnavailable,
                "cloud_demo_reset_token_not_configured",
                "Cloud demo reset token is not configured.");
        }

        if (!CloudDemoCredentials.TokenMatches(context.Request, CloudDemoConfiguration.ResetTokenHeader, resetToken))
        {
            var statusCode = CloudDemoCredentials.HasPresentedToken(context.Request, CloudDemoConfiguration.ResetTokenHeader)
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status401Unauthorized;
            return JsonProblem(
                statusCode,
                "cloud_demo_reset_token_invalid",
                "Cloud demo reset token is required.");
        }

        await seeder.ResetWritableAsync(context.RequestAborted).ConfigureAwait(false);

        var response = new CloudDemoResetResponse(
            "seeded-clean",
            WritableServiceId,
            WritableContractLayerId,
            DateTimeOffset.UtcNow);
        return Results.Json(
            response,
            CloudDemoJsonContext.Default.CloudDemoResetResponse,
            contentType: "application/json");
    }

    private static async Task<IResult> HandleIncidentSseAsync(
        HttpContext context,
        [FromServices] IConfiguration configuration)
    {
        if (!CloudDemoConfiguration.IsEnabled(configuration))
        {
            AddNoStoreHeaders(context.Response);
            return JsonProblem(
                StatusCodes.Status404NotFound,
                "cloud_demo_disabled",
                "Cloud demo services are not enabled.");
        }

        var query = context.Request.Query;
        var sourceId = GetQueryValue(query["sourceId"]) ?? DefaultIncidentSourceId;
        var layerId = GetQueryValue(query["layerId"]) ?? DefaultIncidentLayerId;
        var requestedCursor = GetQueryValue(query["cursor"]);
        var once = string.Equals(GetQueryValue(query["once"]), "true", StringComparison.OrdinalIgnoreCase);
        var stale = string.Equals(GetQueryValue(query["stale"]), "true", StringComparison.OrdinalIgnoreCase);

        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers.Expires = "0";
        response.Headers["X-Accel-Buffering"] = "no";

        await response.WriteAsync("retry: 3000\n\n", context.RequestAborted).ConfigureAwait(false);

        var timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var firstCursor = string.IsNullOrWhiteSpace(requestedCursor)
            ? "cloud-demo-cursor-0001"
            : requestedCursor;
        var checkpoint = new CloudDemoRealtimeCheckpoint(firstCursor, timestamp, timestamp, 1);
        var status = new CloudDemoRealtimeStatusEvent(
            "status",
            stale ? "stale" : "live",
            "cloud-demo-status-0001",
            checkpoint.Cursor,
            checkpoint.Watermark,
            checkpoint.Timestamp,
            checkpoint.Sequence,
            checkpoint,
            stale ? "requested-stale-smoke" : null,
            stale ? 3000 : null);

        await WriteSseEventAsync(
                response,
                "status",
                status,
                CloudDemoJsonContext.Default.CloudDemoRealtimeStatusEvent,
                context.RequestAborted)
            .ConfigureAwait(false);

        var features = CreateIncidentPatches(sourceId);
        var snapshotCheckpoint = checkpoint with
        {
            Cursor = string.Equals(firstCursor, requestedCursor, StringComparison.Ordinal)
                ? "cloud-demo-replay-0002"
                : "cloud-demo-cursor-0002",
            Sequence = 2
        };
        var snapshot = new CloudDemoRealtimeSnapshotEvent(
            "snapshot",
            "cloud-demo-snapshot-0002",
            features,
            true,
            snapshotCheckpoint.Cursor,
            snapshotCheckpoint.Watermark,
            snapshotCheckpoint.Timestamp,
            snapshotCheckpoint.Sequence,
            snapshotCheckpoint);

        await WriteSseEventAsync(
                response,
                "snapshot",
                snapshot,
                CloudDemoJsonContext.Default.CloudDemoRealtimeSnapshotEvent,
                context.RequestAborted)
            .ConfigureAwait(false);

        var heartbeatCheckpoint = snapshotCheckpoint with
        {
            Cursor = "cloud-demo-heartbeat-0003",
            Sequence = 3
        };
        await WriteSseEventAsync(
                response,
                "heartbeat",
                new CloudDemoRealtimeHeartbeatEvent(
                    "heartbeat",
                    "cloud-demo-heartbeat-0003",
                    heartbeatCheckpoint.Cursor,
                    heartbeatCheckpoint.Watermark,
                    heartbeatCheckpoint.Timestamp,
                    heartbeatCheckpoint.Sequence,
                    heartbeatCheckpoint),
                CloudDemoJsonContext.Default.CloudDemoRealtimeHeartbeatEvent,
                context.RequestAborted)
            .ConfigureAwait(false);

        if (once)
        {
            return Results.Empty;
        }

        var sequence = 4L;
        while (!context.RequestAborted.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), context.RequestAborted).ConfigureAwait(false);
            var heartbeatTimestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var liveCheckpoint = new CloudDemoRealtimeCheckpoint(
                $"cloud-demo-heartbeat-{sequence:0000}",
                heartbeatTimestamp,
                heartbeatTimestamp,
                sequence);
            await WriteSseEventAsync(
                    response,
                    "heartbeat",
                    new CloudDemoRealtimeHeartbeatEvent(
                        "heartbeat",
                        $"cloud-demo-heartbeat-{sequence:0000}",
                        liveCheckpoint.Cursor,
                        liveCheckpoint.Watermark,
                        liveCheckpoint.Timestamp,
                        liveCheckpoint.Sequence,
                        liveCheckpoint),
                    CloudDemoJsonContext.Default.CloudDemoRealtimeHeartbeatEvent,
                    context.RequestAborted)
                .ConfigureAwait(false);
            sequence++;
        }

        return Results.Empty;
    }

    private static IResult JsonProblem(int statusCode, string code, string message)
    {
        var response = new CloudDemoProblemResponse(code, message);
        return Results.Json(
            response,
            CloudDemoJsonContext.Default.CloudDemoProblemResponse,
            statusCode: statusCode,
            contentType: "application/json");
    }

    private static async Task WriteSseEventAsync<TValue>(
        HttpResponse response,
        string eventName,
        TValue value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TValue> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync("event: ", cancellationToken).ConfigureAwait(false);
        await response.WriteAsync(eventName, cancellationToken).ConfigureAwait(false);
        await response.WriteAsync("\n", cancellationToken).ConfigureAwait(false);
        await response.WriteAsync("data: ", cancellationToken).ConfigureAwait(false);
        await JsonSerializer.SerializeAsync(response.Body, value, jsonTypeInfo, cancellationToken).ConfigureAwait(false);
        await response.WriteAsync("\n\n", cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddNoStoreHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers.Expires = "0";
    }

    private static string? GetQueryValue(StringValues values)
        => StringValues.IsNullOrEmpty(values) ? null : values.ToString();

    private static CloudDemoRealtimeFeaturePatch[] CreateIncidentPatches(string sourceId)
    {
        var incidents = CreateIncidents();
        var patches = new CloudDemoRealtimeFeaturePatch[incidents.Length];
        for (var index = 0; index < incidents.Length; index++)
        {
            var incident = incidents[index];
            patches[index] = new CloudDemoRealtimeFeaturePatch(
                incident.Id,
                sourceId,
                incident,
                index + 1,
                incident.UpdatedAt);
        }

        return patches;
    }

    private static CloudDemoIncidentFeature[] CreateIncidents()
    {
        return
        [
            new(
                "INC-1001",
                "Harbor fuel sheen",
                "Environmental",
                "critical",
                "open",
                "Hazmat 3",
                "2026-05-05T18:00:00.000Z",
                "2026-05-05T17:34:00.000Z",
                [-157.8722, 21.3074],
                8,
                7,
                "Fuel sheen detected near Pier 2 with harbor patrol holding a containment perimeter.",
                [
                    new("CASE-7781", "Environmental case", "Active"),
                    new("UNIT-HZ3", "Hazmat 3 assignment", "Enroute")
                ],
                [
                    new("ATT-2001", "Pier 2 drone still", "image"),
                    new("ATT-2002", "Initial water sample", "lab")
                ]),
            new(
                "INC-1002",
                "Kakaako grid outage",
                "Utilities",
                "high",
                "assigned",
                "Utility Liaison",
                "2026-05-05T17:55:00.000Z",
                "2026-05-05T17:20:00.000Z",
                [-157.8558, 21.2994],
                14,
                12,
                "Partial feeder outage affecting signals and two public-safety facilities.",
                [
                    new("CASE-7782", "Utility ticket", "Assigned"),
                    new("ASSET-412", "Kakaako feeder", "Impacted")
                ],
                [
                    new("ATT-2010", "Feeder telemetry extract", "table")
                ]),
            new(
                "INC-1003",
                "Ala Moana signal failure",
                "Traffic",
                "medium",
                "monitoring",
                "Traffic 1",
                "2026-05-05T17:48:00.000Z",
                "2026-05-05T17:02:00.000Z",
                [-157.8444, 21.2917],
                22,
                3,
                "Signal timing fault is producing congestion at the mall access road.",
                [
                    new("CASE-7783", "Traffic control log", "Monitoring")
                ],
                [
                    new("ATT-2020", "Intersection camera clip", "video")
                ])
        ];
    }
}
