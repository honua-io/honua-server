// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.Models;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Console;

/// <summary>
/// Anonymous-capable Console Share endpoints: public-link resolution and embed
/// token redemption. These are intentionally public (the opaque token is the
/// bearer of authorization) and therefore enforce strict anonymous-safe denial:
/// unknown, expired, revoked, or no-longer-covered tokens all return an
/// identical 404 that never discloses item identity, titles, or existence.
/// </summary>
/// <remarks>
/// Successful resolution returns only presentation metadata intended for a
/// shared item (id, type, title, summary, endpoints). Owner, provenance, caller
/// permissions, and other private fields are never included on this surface.
/// </remarks>
internal static class ConsoleSharePublicEndpoints
{
    // A single, constant denial body for every link-denial path so the responses
    // are byte-for-byte indistinguishable (no title/existence oracle).
    private const string LinkDeniedMessage = "Link not found or expired.";
    private const string EmbedDeniedMessage = "Embed token not found or expired.";
    private const string PublicItemDeniedMessage = "Shared item not found.";

    /// <summary>
    /// Log category for the anonymous Console Share endpoints.
    /// </summary>
    internal sealed class ConsoleSharePublicEndpointsLogCategory;

    /// <summary>
    /// Maps the anonymous Console Share endpoints into the supplied builder.
    /// </summary>
    public static void MapConsoleSharePublicEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // AllowAnonymous is deliberate: the opaque token authorizes the read, so
        // these routes intentionally bypass admin authorization. The decision is
        // declared here for authorization-policy tooling.
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/console/share")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Console")
            .AllowAnonymous();

        group.MapGet("/link/{token}", HandleResolvePublicLink)
            .WithDisplayName("Resolve Console Public-Link Token")
            .WithSummary("Resolves a public-link token to an anonymous-safe presentation projection. Unknown/expired/revoked tokens return 404 without leaking item identity.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/content/{id}", HandleResolvePublicContent)
            .WithDisplayName("Resolve Console Public Content Item")
            .WithSummary("Resolves a public-indexed content item to an anonymous-safe presentation projection. Private, missing, or no-longer-public items return 404 without leaking titles.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/embed/{token}/redeem", HandleRedeemEmbedToken)
            .WithDisplayName("Redeem Console Embed Token")
            .WithSummary("Redeems an embed token to an anonymous-safe embed resolution. Expired tokens or disabled embedding return 404.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
    }

    private static async Task<Results<Ok<ApiResponse<ConsolePublicLinkResolutionResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleResolvePublicLink(
        string token,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] ILogger<ConsoleSharePublicEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var resolution = await shareStore.ResolvePublicLinkAsync(token, context.RequestAborted).ConfigureAwait(false);

            // Token unknown/expired/revoked, OR the tier was reverted so it no
            // longer covers public-link access — deny uniformly.
            if (resolution is null || !CoversPublicLink(resolution.AccessTier))
            {
                ConsoleShareEndpointsLog.PublicLinkResolveDenied(logger);
                return TypedResults.NotFound(ApiResponse<object>.Failure(LinkDeniedMessage));
            }

            var item = await contentStore.GetAsync(resolution.ItemId, context.RequestAborted).ConfigureAwait(false);
            if (item is null)
            {
                ConsoleShareEndpointsLog.PublicLinkResolveDenied(logger);
                return TypedResults.NotFound(ApiResponse<object>.Failure(LinkDeniedMessage));
            }

            ConsoleShareEndpointsLog.PublicLinkResolveGranted(logger, resolution.TokenId, resolution.ItemId);
            return TypedResults.Ok(ApiResponse<ConsolePublicLinkResolutionResponse>.CreateSuccess(new ConsolePublicLinkResolutionResponse
            {
                ItemId = item.Id,
                ItemType = item.ItemType,
                Title = item.Title,
                Summary = item.Description,
                AccessTier = resolution.AccessTier,
                Endpoints = ConsoleShareMapper.BuildEndpoints(item.Id),
            }));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never surface raw exception detail on the anonymous surface.
            ConsoleEndpointsLog.EndpointFailed(logger, "share.public.link", ex);
            return TypedResults.Problem(
                title: "Console public-link resolution failed",
                detail: "An internal error occurred while resolving the public-link token.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<ConsolePublicLinkResolutionResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleResolvePublicContent(
        string id,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] ILogger<ConsoleSharePublicEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var item = await contentStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (item is null)
            {
                ConsoleShareEndpointsLog.PublicContentResolveDenied(logger);
                return TypedResults.NotFound(ApiResponse<object>.Failure(PublicItemDeniedMessage));
            }

            var state = await shareStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            var effectiveTier = ConsoleShareMapper.ResolveEffectiveTier(state, item);
            if (effectiveTier != ConsoleShareAccessTier.PublicIndexed)
            {
                ConsoleShareEndpointsLog.PublicContentResolveDenied(logger);
                return TypedResults.NotFound(ApiResponse<object>.Failure(PublicItemDeniedMessage));
            }

            ConsoleShareEndpointsLog.PublicContentResolveGranted(logger, item.Id);
            return TypedResults.Ok(ApiResponse<ConsolePublicLinkResolutionResponse>.CreateSuccess(new ConsolePublicLinkResolutionResponse
            {
                ItemId = item.Id,
                ItemType = item.ItemType,
                Title = item.Title,
                Summary = item.Description,
                AccessTier = effectiveTier,
                Endpoints = ConsoleShareMapper.BuildEndpoints(item.Id),
            }));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "share.public.content", ex);
            return TypedResults.Problem(
                title: "Console public content resolution failed",
                detail: "An internal error occurred while resolving the public content item.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleEmbedRedeemResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleRedeemEmbedToken(
        string token,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] ILogger<ConsoleSharePublicEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var resolution = await shareStore.RedeemEmbedTokenAsync(token, context.RequestAborted).ConfigureAwait(false);
            if (resolution is null)
            {
                ConsoleShareEndpointsLog.EmbedRedeemDenied(logger);
                return TypedResults.NotFound(ApiResponse<object>.Failure(EmbedDeniedMessage));
            }

            var item = await contentStore.GetAsync(resolution.ItemId, context.RequestAborted).ConfigureAwait(false);
            if (item is null)
            {
                ConsoleShareEndpointsLog.EmbedRedeemDenied(logger);
                return TypedResults.NotFound(ApiResponse<object>.Failure(EmbedDeniedMessage));
            }

            ConsoleShareEndpointsLog.EmbedRedeemGranted(logger, resolution.TokenId, resolution.ItemId, resolution.Audience);
            return TypedResults.Ok(ApiResponse<ConsoleEmbedRedeemResponse>.CreateSuccess(new ConsoleEmbedRedeemResponse
            {
                ItemId = item.Id,
                ItemType = item.ItemType,
                Title = item.Title,
                Audience = resolution.Audience,
                ExpiresAt = resolution.ExpiresAt,
                Endpoints = ConsoleShareMapper.BuildEndpoints(item.Id),
            }));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "share.public.embed", ex);
            return TypedResults.Problem(
                title: "Console embed redemption failed",
                detail: "An internal error occurred while redeeming the embed token.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static bool CoversPublicLink(ConsoleShareAccessTier tier)
        => tier is ConsoleShareAccessTier.PublicLink or ConsoleShareAccessTier.PublicIndexed;
}
