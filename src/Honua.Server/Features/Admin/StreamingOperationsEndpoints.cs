// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.WebSockets;
using System.Globalization;
using System.Text.Json;
using Honua.Alerts;
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
            // GA: no capability gate; admin authorization still required below.
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Operations")
            .RequireAdminAuthorization()
            .RequirePlatformAdminAuthority();

        group.MapGet("/subscribers", HandleListSubscribers)
            .WithDisplayName("List Streaming Subscribers")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<SubscriberListResponse>>();

        group.MapGet("/alerts", HandleAlertStream)
            .AddEndpointFilter<LiveStreamAuthorizationFilter>()
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
        [FromServices] RealtimeAlertNotificationBroadcaster subscriptionManager,
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

    internal static async Task<IResult> HandleAlertStream(
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
        await using var subscription = await broadcaster.SubscribeAsync(
            CreateAlertDeliveryHandler(webSocket, sendLock),
            new SubscriptionOptions(context.Request.Query["clientLabel"].ToString(), after),
            context.RequestAborted).ConfigureAwait(false);

        Task<WebSocketReceiveResult>? pendingReceive = null;
        try
        {
            pendingReceive = await RunAlertReceiveLoopAsync(webSocket, subscription, context.RequestAborted)
                .ConfigureAwait(false);
        }
        finally
        {
            var closeReason = subscription.DisconnectToken.IsCancellationRequested
                ? "Subscription disconnected."
                : "Stream closed.";
            await subscription.DisposeAsync().ConfigureAwait(false);
            await CloseAlertStreamAsync(
                new AlertStreamCloseContext
                {
                    Socket = webSocket,
                    SendLock = sendLock,
                    PendingReceive = pendingReceive,
                    Reset = subscription.Reset,
                    CloseReason = closeReason
                },
                context.RequestAborted).ConfigureAwait(false);
        }

        return Results.Empty;
    }

    private sealed class AlertStreamCloseContext
    {
        public required WebSocket Socket { get; init; }
        public required SemaphoreSlim SendLock { get; init; }
        public Task<WebSocketReceiveResult>? PendingReceive { get; init; }
        public Honua.Core.Features.Realtime.RealtimeDelivery.Reset? Reset { get; init; }
        public required string CloseReason { get; init; }
    }

    private static Func<Honua.Core.Features.Alerts.Domain.AlertEventEnvelope, Honua.Core.Features.Realtime.RealtimePosition, CancellationToken, Task>
        CreateAlertDeliveryHandler(WebSocket webSocket, SemaphoreSlim sendLock)
        => async (alertEvent, position, cancellationToken) =>
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
                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.SendAsync(SerializeAlertFrame(alertEvent, position),
                        WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
            {
                // The receive loop will clean up disconnected clients.
            }
            finally
            {
                if (lockTaken)
                {
                    sendLock.Release();
                }
            }
        };

    private static async Task<Task<WebSocketReceiveResult>?> RunAlertReceiveLoopAsync(
        WebSocket webSocket,
        RealtimeAlertNotificationBroadcaster.Subscription subscription,
        CancellationToken requestAborted)
    {
        var buffer = new byte[1];
        Task<WebSocketReceiveResult>? pendingReceive = null;
        try
        {
            while (webSocket.State == WebSocketState.Open && !subscription.DisconnectToken.IsCancellationRequested)
            {
                pendingReceive = webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), requestAborted);
                var result = await pendingReceive.WaitAsync(subscription.DisconnectToken).ConfigureAwait(false);
                pendingReceive = null;
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (subscription.DisconnectToken.IsCancellationRequested || requestAborted.IsCancellationRequested)
        {
            // Expected: the subscription was disconnected or the client aborted the request.
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            // Client disconnected or transport closed.
        }

        return pendingReceive;
    }

    private static async Task CloseAlertStreamAsync(
        AlertStreamCloseContext closeContext,
        CancellationToken requestAborted)
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        shutdown.CancelAfter(TimeSpan.FromSeconds(5));
        var pendingReceive = closeContext.PendingReceive;
        try
        {
            if (closeContext.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await SendCloseOutputLockedAsync(closeContext, shutdown.Token).ConfigureAwait(false);
                pendingReceive = await AwaitCloseHandshakeAsync(closeContext.Socket, pendingReceive, shutdown.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException or IOException)
        {
            closeContext.Socket.Abort();
        }
        finally
        {
            await AbortPendingReceiveAsync(closeContext.Socket, pendingReceive).ConfigureAwait(false);
        }
    }

    private static async Task SendCloseOutputLockedAsync(
        AlertStreamCloseContext closeContext,
        CancellationToken cancellationToken)
    {
        await closeContext.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (closeContext.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await SendResetAsync(closeContext.Socket, closeContext.Reset, cancellationToken).ConfigureAwait(false);
                await closeContext.Socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    closeContext.CloseReason,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            closeContext.SendLock.Release();
        }
    }

    private static async Task<Task<WebSocketReceiveResult>?> AwaitCloseHandshakeAsync(
        WebSocket socket,
        Task<WebSocketReceiveResult>? pendingReceive,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        while (socket.State == WebSocketState.CloseSent)
        {
            pendingReceive ??= socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            await pendingReceive.WaitAsync(cancellationToken).ConfigureAwait(false);
            pendingReceive = null;
        }

        return pendingReceive;
    }

    private static async Task AbortPendingReceiveAsync(
        WebSocket socket,
        Task<WebSocketReceiveResult>? pendingReceive)
    {
        if (pendingReceive is null)
        {
            return;
        }

        socket.Abort();
        try
        {
            await pendingReceive.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException or IOException)
        {
            // Observe the receive canceled by the shutdown deadline or request abort.
        }
    }

    private static async Task SendResetAsync(WebSocket socket,
        Honua.Core.Features.Realtime.RealtimeDelivery.Reset? reset, CancellationToken cancellationToken)
    {
        if (reset is null)
        {
            return;
        }
        using var payload = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payload))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "reset");
            writer.WriteString("reason", reset.Reason);
            writer.WriteNumber("head", reset.Head);
            writer.WriteEndObject();
        }
        await socket.SendAsync(payload.ToArray(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
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
        [FromServices] RealtimeAlertNotificationBroadcaster subscriptionManager,
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
