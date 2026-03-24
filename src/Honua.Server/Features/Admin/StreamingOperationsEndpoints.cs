// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.WebSockets;
using System.Text.Json;
using Honua.Server.Features.Alerts;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for monitoring and managing streaming subscribers.
/// </summary>
internal static class StreamingOperationsEndpoints
{
    public static void MapStreamingOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/operations/streaming")
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
        [FromServices] IAlertNotificationBroadcaster broadcaster,
        HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "WebSocket upgrade required.");
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var sendLock = new SemaphoreSlim(1, 1);
        using var subscription = broadcaster.Subscribe(
            async (alertEvent, cancellationToken) =>
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

                    var payload = JsonSerializer.SerializeToUtf8Bytes(
                        alertEvent,
                        StreamingOperationsJsonContext.Default.AlertEventEnvelope);
                    await webSocket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
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
            new SubscriptionOptions(context.Request.Query["clientLabel"].ToString()));

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
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        return Results.Empty;
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

/// <summary>
/// Log category for streaming operations endpoints.
/// </summary>
internal sealed class StreamingOperationsEndpointsLog;
