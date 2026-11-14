// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.WebSockets;
using System.Globalization;
using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Capabilities;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

internal static class StreamingOperationsEndpoints
{
    public static void MapStreamingOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/operations/streaming")
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
            // Promoted to GA in #2428 — no capability gate; admin authorization still
            // required below.
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Operations")
            .RequireAdminAuthorization();

        group.MapGet("/subscribers", HandleListSubscribers)
            .WithDisplayName("List Streaming Subscribers")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<SubscriberListResponse>>();

        group.MapGet("/alerts", HandleAlertStream)
            .WithDisplayName("Stream Alert Notifications")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .WithMetadata(WebSocketEndpointMetadata.Instance)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapDelete("/subscribers/{subscriberId:guid}", HandleDisconnectSubscriber)
            .WithDisplayName("Disconnect Streaming Subscriber")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }))
            .Produces<ApiResponse<object>>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static IResult HandleListSubscribers(
        [FromServices] IStreamingSubscriptionManager subscriptionManager,
        ILogger<StreamingOperationsEndpointsLog> logger)
    {
        var subscriptions = subscriptionManager.GetSubscriptions();
        var now = DateTimeOffset.UtcNow;

        var subscribers = subscriptions.Select(s => new SubscriberInfoResponse
        {
            SubscriberId = s.SubscriberId,
            ConnectedAt = s.ConnectedAt,
            ClientLabel = s.ClientLabel,
            DurationSeconds = (now - s.ConnectedAt).TotalSeconds
        }).ToArray();

        AdminLog.StreamingSubscribersListed(logger, subscribers.Length);

        var response = new SubscriberListResponse
        {
            SubscriberCount = subscribers.Length,
            Subscribers = subscribers,
            GeneratedAt = now
        };

        return Results.Json(
            ApiResponse<SubscriberListResponse>.CreateSuccess(response),
            StreamingOperationsJsonContext.Default.ApiResponseSubscriberListResponse);
    }

    private static async Task<IResult> HandleAlertStream(
        [FromServices] Honua.Alerts.RealtimeAlertNotificationBroadcaster broadcaster,
        HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "WebSocket upgrade required.");
        }

        if (!TryParseAlertCursor(context.Request.Query["cursor"].ToString(), out var after))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context,
                StatusCodes.Status400BadRequest, "Expected cursor=alerts:<sequence>.");
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var sendLock = new SemaphoreSlim(1, 1);
        using var subscription = await broadcaster.SubscribeAsync(
            async (alertEvent, position, cancellationToken) =>
            {
                if (webSocket.State != WebSocketState.Open)
                {
                    return;
                }

                var lockTaken = false;
                try
                {
                    await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    lockTaken = true;

                    if (webSocket.State != WebSocketState.Open)
                    {
                        return;
                    }

                    await webSocket.SendAsync(SerializeAlertFrame(alertEvent, position),
                        WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (WebSocketException)
                {
                    // The receive loop will clean up disconnected clients.
                }
                catch (ObjectDisposedException)
                {
                    // The receive loop already closed the socket.
                }
                finally
                {
                    if (lockTaken)
                    {
                        sendLock.Release();
                    }
                }
            },
            new SubscriptionOptions(context.Request.Query["clientLabel"].ToString(), after), context.RequestAborted).ConfigureAwait(false);

        var buffer = new byte[1];
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted,
                subscription.DisconnectToken);

            while (webSocket.State == WebSocketState.Open && !linkedCts.Token.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(buffer, linkedCts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (subscription.DisconnectToken.IsCancellationRequested || context.RequestAborted.IsCancellationRequested)
        {
            // Expected: the subscription was disconnected or the client aborted the request.
        }
        finally
        {
            if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        subscription.DisconnectToken.IsCancellationRequested
                            ? "Subscription disconnected."
                            : "Stream closed.",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    // Best-effort close; the remote peer may have already reset the connection.
                }
                catch (ObjectDisposedException)
                {
                    // Best-effort close; the socket may already have been disposed concurrently.
                }
            }
        }

        return Results.Empty;
    }

    private static bool TryParseAlertCursor(string cursor, out long? after)
    {
        after = null;
        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }
        if (!cursor.StartsWith("alerts:", StringComparison.Ordinal) ||
            !long.TryParse(cursor.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
        {
            return false;
        }
        after = sequence;
        return true;
    }

    private static byte[] SerializeAlertFrame(Honua.Core.Features.Alerts.Domain.AlertEventEnvelope alertEvent,
        Honua.Core.Features.Realtime.RealtimePosition position)
    {
        using var payload = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payload))
        {
            writer.WriteStartObject();
            var envelope = JsonSerializer.SerializeToElement(alertEvent,
                StreamingOperationsJsonContext.Default.AlertEventEnvelope);
            foreach (var property in envelope.EnumerateObject())
            {
                property.WriteTo(writer);
            }
            writer.WriteString("cursor", position.ToString());
            writer.WriteEndObject();
        }
        return payload.ToArray();
    }

    private static IResult HandleDisconnectSubscriber(
        Guid subscriberId,
        [FromServices] IStreamingSubscriptionManager subscriptionManager,
        HttpContext context,
        ILogger<StreamingOperationsEndpointsLog> logger)
    {
        var disconnected = subscriptionManager.DisconnectSubscriber(subscriberId);

        if (!disconnected)
        {
            AdminLog.SubscriberNotFound(logger, subscriberId);
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                string.Concat("Subscriber ", subscriberId.ToString(), " not found."));
        }

        AdminLog.SubscriberDisconnected(logger, subscriberId);

        return Results.Json(
            ApiResponse<object>.SuccessWithMessage(string.Concat("Subscriber ", subscriberId.ToString(), " disconnected.")),
            StreamingOperationsJsonContext.Default.ApiResponseObject);
    }
}

internal sealed class StreamingOperationsEndpointsLog;
