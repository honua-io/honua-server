// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
