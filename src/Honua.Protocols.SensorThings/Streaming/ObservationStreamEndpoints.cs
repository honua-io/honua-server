// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Protocols.SensorThings.Streaming;

/// <summary>
/// OGC SensorThings real-time observation streaming endpoint (Phase 3, #1747).
/// A single route serves both Server-Sent Events (<c>Accept: text/event-stream</c>) and
/// WebSocket (upgrade request) transports, reusing the bounded-channel session pattern of
/// the server feature-change stream. Newly-ingested observations (Phase 2) are pushed to
/// connected clients in near-real-time without an MQTT broker dependency.
/// </summary>
internal static class ObservationStreamEndpoints
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(25);

    /// <summary>Maps the SensorThings observation-stream endpoint.</summary>
    public static IEndpointRouteBuilder MapSensorThingsStreamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/sta/v1.1/ObservationsStream", HandleStream)
            .WithDisplayName("STA Observation Stream")
            .WithName("StaObservationStream")
            .WithSummary("Stream new Observations in real time (SSE or WebSocket)")
            .WithDescription(
                "Opens a real-time stream of newly-ingested Observations. SSE: send " +
                "Accept: text/event-stream. WebSocket: send an Upgrade request. " +
                "Optional query param: datastreamId (tail a single Datastream).")
            .WithTags("SensorThings")
            .Produces(200, contentType: "text/event-stream")
            .Produces(400);

        return endpoints;
    }

    private static async Task<IResult> HandleStream(
        HttpContext context,
        [FromServices] ObservationStreamSessionManager sessionManager)
    {
        long? datastreamId = null;
        if (context.Request.Query.TryGetValue("datastreamId", out var raw) &&
            long.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            datastreamId = parsed;
        }

        var isWebSocket = context.WebSockets.IsWebSocketRequest;
        var accept = context.Request.Headers.Accept.ToString();
        var isSse = accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

        if (!isWebSocket && !isSse)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "WebSocket upgrade or Accept: text/event-stream header required.");
        }

        if (isWebSocket)
        {
            await HandleWebSocketAsync(context, sessionManager, datastreamId).ConfigureAwait(false);
        }
        else
        {
            await HandleSseAsync(context, sessionManager, datastreamId).ConfigureAwait(false);
        }

        return Results.Empty;
    }

    private static async Task HandleSseAsync(
        HttpContext context,
        ObservationStreamSessionManager sessionManager,
        long? datastreamId)
    {
        var session = sessionManager.TryCreateSession("SSE", datastreamId);
        if (session is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        using var sessionLease = session;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, session.DisconnectToken);
        var ct = linkedCts.Token;

        try
        {
            await WriteSseAsync(
                context.Response,
                "status",
                JsonSerializer.Serialize(
                    new ObservationStreamStatus { Status = "connected", DatastreamId = datastreamId },
                    ObservationStreamJsonContext.Default.ObservationStreamStatus),
                ct).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var readTask = session.Reader.WaitToReadAsync(readCts.Token).AsTask();
                var timeoutTask = Task.Delay(HeartbeatInterval, readCts.Token);
                var completed = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);
                readCts.Cancel();

                if (completed == timeoutTask)
                {
                    await SwallowAsync(readTask).ConfigureAwait(false);
                    await WriteSseAsync(context.Response, "heartbeat", "{}", ct).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                    continue;
                }

                await SwallowAsync(timeoutTask).ConfigureAwait(false);
                if (!await readTask.ConfigureAwait(false))
                {
                    break;
                }

                while (session.Reader.TryRead(out var frame))
                {
                    if (frame.IsHeartbeat)
                    {
                        await WriteSseAsync(context.Response, "heartbeat", "{}", ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var json = JsonSerializer.Serialize(frame, ObservationStreamJsonContext.Default.ObservationStreamFrame);
                        await WriteSseAsync(context.Response, "observation", json, ct).ConfigureAwait(false);
                    }
                }

                await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal disconnect.
        }
        catch (IOException)
        {
            // Client disconnected.
        }
        catch (ObjectDisposedException)
        {
            // Response disposed.
        }
    }

    private static async Task HandleWebSocketAsync(
        HttpContext context,
        ObservationStreamSessionManager sessionManager,
        long? datastreamId)
    {
        var session = sessionManager.TryCreateSession("WebSocket", datastreamId);
        if (session is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        using var sessionLease = session;
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, session.DisconnectToken);
        var ct = linkedCts.Token;

        try
        {
            await SendSocketAsync(
                socket,
                JsonSerializer.Serialize(
                    new ObservationStreamStatus { Status = "connected", DatastreamId = datastreamId },
                    ObservationStreamJsonContext.Default.ObservationStreamStatus),
                ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var readTask = session.Reader.WaitToReadAsync(readCts.Token).AsTask();
                var timeoutTask = Task.Delay(HeartbeatInterval, readCts.Token);
                var completed = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);
                readCts.Cancel();

                if (completed == timeoutTask)
                {
                    await SwallowAsync(readTask).ConfigureAwait(false);
                    await SendSocketAsync(socket, "{\"heartbeat\":true}", ct).ConfigureAwait(false);
                    continue;
                }

                await SwallowAsync(timeoutTask).ConfigureAwait(false);
                if (!await readTask.ConfigureAwait(false))
                {
                    break;
                }

                while (session.Reader.TryRead(out var frame))
                {
                    if (frame.IsHeartbeat)
                    {
                        await SendSocketAsync(socket, "{\"heartbeat\":true}", ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var json = JsonSerializer.Serialize(frame, ObservationStreamJsonContext.Default.ObservationStreamFrame);
                        await SendSocketAsync(socket, json, ct).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal disconnect.
        }
        catch (WebSocketException)
        {
            // Client closed abruptly.
        }
    }

    private static Task WriteSseAsync(HttpResponse response, string eventName, string json, CancellationToken ct) =>
        response.WriteAsync(string.Concat("event: ", eventName, "\ndata: ", json, "\n\n"), ct);

    private static Task SendSocketAsync(WebSocket socket, string json, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, ct);

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the alternate waiter is cancelled.
        }
    }
}
