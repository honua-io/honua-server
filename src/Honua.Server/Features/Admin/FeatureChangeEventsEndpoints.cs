// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin replay endpoints for normalized feature-change events.
/// </summary>
internal static class FeatureChangeEventsEndpoints
{
    public static void MapFeatureChangeEventsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/feature-events")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Feature Events")
            .WithDescription("Feature change event replay endpoints")
            .RequireAdminAuthorization();

        _ = group.Map("/replay", HandleReplay)
            .WithName("ReplayFeatureEvents")
            .WithSummary("Replay feature-change events")
            .WithDescription("Replays feature-change events by cursor and/or time window for downstream recovery")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]));
    }

    private static async Task<IResult> HandleReplay(
        [FromServices] IFeatureChangeEventStore store,
        long? cursor = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || limit > 1_000)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "Query parameter 'limit' must be between 1 and 1000.");
        }

        if (from.HasValue && to.HasValue && from > to)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "Query parameter 'from' must be earlier than or equal to 'to'.");
        }

        var items = await store.QueryAsync(
            cursor,
            from,
            to,
            limit + 1,
            cancellationToken).ConfigureAwait(false);

        var hasMore = items.Count > limit;
        var page = hasMore ? items.Take(limit).ToArray() : items.ToArray();
        var nextCursor = page.Length > 0 ? page[^1].Cursor : cursor;

        var response = new FeatureEventReplayResponse
        {
            Events = page,
            NextCursor = nextCursor,
            HasMore = hasMore
        };

        return Results.Json(response, FeatureEventReplayJsonContext.Default.FeatureEventReplayResponse);
    }
}

internal sealed record FeatureEventReplayResponse
{
    public required FeatureChangeEvent[] Events { get; init; }
    public long? NextCursor { get; init; }
    public required bool HasMore { get; init; }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.General)]
[System.Text.Json.Serialization.JsonSerializable(typeof(FeatureEventReplayResponse))]
[System.Text.Json.Serialization.JsonSerializable(typeof(FeatureChangeEvent))]
[System.Text.Json.Serialization.JsonSerializable(typeof(FeatureChangeEvent[]))]
internal sealed partial class FeatureEventReplayJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
