// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Studio.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Studio;

/// <summary>
/// Studio published-route resolver (honua-server#4907). An approved Studio publication reports
/// <c>activeUrl</c> = <see cref="StudioPublishedRoutes.BuildActiveUrl"/> of its intent route;
/// this endpoint resolves that route to the governing accepted publication and serves the
/// content item's Active version -- the published pointer, so a republish or rollback is
/// reflected and a superseded version is never served -- honouring the publication visibility.
/// </summary>
internal static class StudioPublishedRouteEndpoints
{
    private const string ResourceType = "studio-published-route";

    /// <summary>Log category for Studio published-route reads.</summary>
    internal sealed class StudioPublishedRouteLog;

    /// <summary>Maps the Studio published-route read endpoint.</summary>
    public static void MapStudioPublishedRouteEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Deliberately not part of the /studio lifecycle group: its
        // RequireStudioLifecycleAuthorization() gate is admin-only by default, and a public
        // publication must open anonymously. Visibility is enforced per read instead.
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/studio/published")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Studio");

        group.MapGet("/{*route}", HandleRead)
            .WithDisplayName("Read Studio Published Route")
            .WithSummary("Resolves a Studio publication route to the content item's Active (published) version, enforcing the publication visibility.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static async Task<IResult> HandleRead(
        string? route,
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] StudioEndpointAuthorization authorization,
        [FromServices] ILogger<StudioPublishedRouteLog> logger,
        HttpContext context)
    {
        // The answer follows the live published pointer, so a rollback must be visible on the
        // very next read and a non-public artifact must never land in a shared cache.
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var routeKey = StudioPublishedRoutes.ToRouteKey(route);
            var publication = await service
                .GetActivePublicationRequestByRouteAsync(routeKey, context.RequestAborted)
                .ConfigureAwait(false);
            var pointers = publication is null
                ? null
                : await service.GetPointersAsync(publication.ItemId, context.RequestAborted).ConfigureAwait(false);
            if (publication is null || pointers?.PublishedVersionId is not { } publishedVersionId)
            {
                return StandardErrorHelpers.CreateNotFound(context, "Published route not found.");
            }

            var visibility = publication.Intent?.Visibility?.Trim();
            if (!IsVisibility(visibility, "public"))
            {
                if (context.User.Identity?.IsAuthenticated != true)
                {
                    return StandardErrorHelpers.CreateUnauthorized(context, "Authentication is required to read this published route.");
                }

                // Organization/team publications admit any authenticated reader through the same
                // published-read tier HandleGetVersion uses; personal (or unspecified) visibility
                // stays owner-or-admin. The admin-only posture is unchanged while end-user Studio
                // authorization is disabled.
                var decision = await authorization.AuthorizeAsync(
                    context,
                    StudioAuthorizationOperation.ReadContentItem,
                    pointers.OwnerId,
                    pointers.TenantId,
                    ResourceType,
                    publication.ItemId.ToString("D"),
                    isPubliclyReadable: IsVisibility(visibility, "organization") || IsVisibility(visibility, "team"))
                    .ConfigureAwait(false);
                if (!decision.IsAllowed)
                {
                    return StandardErrorHelpers.CreateForbidden(
                        context,
                        decision.Reason ?? "The caller is not authorized to read this published route.");
                }
            }

            var version = await service
                .GetVersionAsync(publication.ItemId, publishedVersionId, context.RequestAborted)
                .ConfigureAwait(false);
            if (version is null)
            {
                return StandardErrorHelpers.CreateNotFound(context, "Published version not found.");
            }

            var artifact = new StudioPublishedArtifact
            {
                Route = routeKey,
                Visibility = visibility,
                PublicationId = publication.RequestId,
                ItemId = version.ItemId,
                VersionId = version.VersionId,
                VersionNumber = version.VersionNumber,
                PackageKey = version.PackageKey,
                Family = version.Envelope.Family,
                ContentHash = version.ContentHash,
                PublishedAt = publication.CreatedAt,
                Envelope = version.Envelope,
            };
            return Results.Json(
                ApiResponse<StudioPublishedArtifact>.CreateSuccess(artifact),
                StudioApiJsonContext.Default.ApiResponseStudioPublishedArtifact);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(logger, "published-route.read", ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "The Studio published route could not be read.");
        }
    }

    private static bool IsVisibility(string? visibility, string expected)
        => string.Equals(visibility, expected, StringComparison.OrdinalIgnoreCase);
}
