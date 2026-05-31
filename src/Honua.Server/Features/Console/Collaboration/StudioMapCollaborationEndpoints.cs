// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.Console.Collaboration.Abstractions;
using Honua.Core.Features.Console.Collaboration.Domain;
using Honua.Server.Features.Console.Collaboration.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Console.Collaboration;

/// <summary>
/// Durable Studio map collaboration endpoints (honua-server#1278, slice 1):
/// feature-pinned comment threads (list/create/reply/resolve) and the collaboration
/// activity feed for a map draft/package. The real-time presence/cursor/markup
/// transport is deferred to a follow-up slice; the Console collaboration surface
/// (#124) keeps rendering its missing-binding state for those live slots until then.
/// All routes require admin authorization (Console management surface).
/// </summary>
internal static class StudioMapCollaborationEndpoints
{
    private const int DefaultActivityLimit = 50;
    private const int MaxActivityLimit = 200;

    /// <summary>Log category for the collaboration endpoints.</summary>
    internal sealed class StudioMapCollaborationLogCategory;

    /// <summary>Maps the durable Studio map collaboration endpoints.</summary>
    public static void MapStudioMapCollaborationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/console/maps/{mapId}/collab")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Console")
            .RequireAdminAuthorization();

        group.MapGet("/comments", HandleListThreads)
            .WithDisplayName("List Studio Map Comment Threads")
            .WithSummary("Lists feature-pinned comment threads for a Studio map draft/package, newest activity first.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/comments", HandleCreateThread)
            .WithDisplayName("Open Studio Map Comment Thread")
            .WithSummary("Opens a feature-pinned comment thread with its first message and anchor metadata.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPost("/comments/{threadId}/replies", HandleAddReply)
            .WithDisplayName("Reply To Studio Map Comment Thread")
            .WithSummary("Appends a reply to an existing comment thread.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPost("/comments/{threadId}/resolve", HandleResolveThread)
            .WithDisplayName("Resolve Studio Map Comment Thread")
            .WithSummary("Resolves or reopens a comment thread.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapGet("/activity", HandleListActivity)
            .WithDisplayName("List Studio Map Activity Feed")
            .WithSummary("Lists durable collaboration activity events (comment lifecycle) for a Studio map draft/package, newest first.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static async Task<Results<Ok<ApiResponse<StudioMapCommentThreadListResponse>>, ProblemHttpResult>> HandleListThreads(
        string mapId,
        [FromServices] IStudioMapCollaborationStore store,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<StudioMapCollaborationLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var threads = await store.ListThreadsAsync(mapId, context.RequestAborted).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            var response = new StudioMapCommentThreadListResponse
            {
                MapId = mapId,
                Threads = threads.Select(t => StudioMapCollaborationMapper.ToThreadDto(t, now)).ToList(),
            };
            return TypedResults.Ok(ApiResponse<StudioMapCommentThreadListResponse>.CreateSuccess(response));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "collab.comments.list", ex);
            return Failure("Failed to list comment threads.");
        }
    }

    private static async Task<Results<Ok<ApiResponse<StudioMapCommentThreadDto>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>> HandleCreateThread(
        string mapId,
        CreateStudioMapCommentThreadRequest request,
        [FromServices] IStudioMapCollaborationStore store,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<StudioMapCollaborationLogCategory> logger,
        HttpContext context)
    {
        try
        {
            if (!TryValidate(request, out var validationError))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(validationError));
            }

            var (actorName, actorId) = ResolveActor(context);
            var thread = await store.CreateThreadAsync(
                mapId,
                request.FeatureLabel,
                request.LayerRef,
                request.XFraction,
                request.YFraction,
                actorName,
                actorId,
                request.Body,
                context.RequestAborted).ConfigureAwait(false);

            var now = timeProvider.GetUtcNow();
            return TypedResults.Ok(ApiResponse<StudioMapCommentThreadDto>.CreateSuccess(
                StudioMapCollaborationMapper.ToThreadDto(thread, now)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "collab.comments.create", ex);
            return Failure("Failed to open comment thread.");
        }
    }

    private static async Task<Results<Ok<ApiResponse<StudioMapCommentThreadDto>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>> HandleAddReply(
        string mapId,
        string threadId,
        CreateStudioMapCommentReplyRequest request,
        [FromServices] IStudioMapCollaborationStore store,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<StudioMapCollaborationLogCategory> logger,
        HttpContext context)
    {
        try
        {
            if (!Guid.TryParse(threadId, out var threadGuid))
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Comment thread not found."));
            }
            if (!TryValidate(request, out var validationError))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(validationError));
            }

            var (actorName, actorId) = ResolveActor(context);
            var thread = await store.AddReplyAsync(
                mapId, threadGuid, actorName, actorId, request.Body, context.RequestAborted).ConfigureAwait(false);
            if (thread is null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Comment thread not found."));
            }

            var now = timeProvider.GetUtcNow();
            return TypedResults.Ok(ApiResponse<StudioMapCommentThreadDto>.CreateSuccess(
                StudioMapCollaborationMapper.ToThreadDto(thread, now)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "collab.comments.reply", ex);
            return Failure("Failed to add reply.");
        }
    }

    private static async Task<Results<Ok<ApiResponse<StudioMapCommentThreadDto>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleResolveThread(
        string mapId,
        string threadId,
        ResolveStudioMapCommentThreadRequest request,
        [FromServices] IStudioMapCollaborationStore store,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<StudioMapCollaborationLogCategory> logger,
        HttpContext context)
    {
        try
        {
            if (!Guid.TryParse(threadId, out var threadGuid))
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Comment thread not found."));
            }

            var (actorName, actorId) = ResolveActor(context);
            var thread = await store.SetResolvedAsync(
                mapId, threadGuid, request.Resolved, actorName, actorId, context.RequestAborted).ConfigureAwait(false);
            if (thread is null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Comment thread not found."));
            }

            var now = timeProvider.GetUtcNow();
            return TypedResults.Ok(ApiResponse<StudioMapCommentThreadDto>.CreateSuccess(
                StudioMapCollaborationMapper.ToThreadDto(thread, now)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "collab.comments.resolve", ex);
            return Failure("Failed to update comment thread.");
        }
    }

    private static async Task<Results<Ok<ApiResponse<StudioMapActivityListResponse>>, ProblemHttpResult>> HandleListActivity(
        string mapId,
        [FromServices] IStudioMapCollaborationStore store,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<StudioMapCollaborationLogCategory> logger,
        HttpContext context,
        [FromQuery] int? limit = null)
    {
        try
        {
            var effectiveLimit = Math.Clamp(limit ?? DefaultActivityLimit, 1, MaxActivityLimit);
            var events = await store.ListActivityAsync(mapId, effectiveLimit, context.RequestAborted).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            var response = new StudioMapActivityListResponse
            {
                MapId = mapId,
                Activity = events.Select(e => StudioMapCollaborationMapper.ToActivityDto(e, now)).ToList(),
            };
            return TypedResults.Ok(ApiResponse<StudioMapActivityListResponse>.CreateSuccess(response));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "collab.activity.list", ex);
            return Failure("Failed to list activity feed.");
        }
    }

    private static (string Name, string? Id) ResolveActor(HttpContext context)
    {
        var actorId = ConsolePrincipal.ResolveActorId(context.User);
        var name = context.User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = actorId;
        }

        return (string.IsNullOrWhiteSpace(name) ? "Unknown" : name, actorId);
    }

    private static ProblemHttpResult Failure(string detail)
        => TypedResults.Problem(
            title: "Studio map collaboration request failed",
            detail: detail,
            statusCode: StatusCodes.Status500InternalServerError);

    private static bool TryValidate<TRequest>(TRequest request, out string error) where TRequest : notnull
    {
        var validationResults = new List<ValidationResult>();
        if (Validator.TryValidateObject(request, new ValidationContext(request), validationResults, validateAllProperties: true))
        {
            error = string.Empty;
            return true;
        }

        error = string.Join(", ", validationResults.Select(r => r.ErrorMessage));
        if (string.IsNullOrEmpty(error))
        {
            error = "Request validation failed.";
        }

        return false;
    }
}
