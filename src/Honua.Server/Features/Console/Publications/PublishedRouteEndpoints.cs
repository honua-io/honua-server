// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Publishing.Content;
using Honua.Core.Features.Publishing.Content.Abstractions;
using Honua.Core.Features.Publishing.Content.Domain;
using Honua.Core.Features.Publishing.Content.Services;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Console.Publications;

/// <summary>
/// Public route resolution endpoint. Resolves a server-owned route slug to a
/// client-safe published artifact view, enforcing visibility/access, public-link, and
/// embed policy on the read. Anonymous-capable; backend reads enforce policy and audit
/// denied public-link/embed attempts.
/// </summary>
internal static class PublishedRouteEndpoints
{
    /// <summary>Log category for published route reads.</summary>
    internal sealed class PublishedRouteLog;

    /// <summary>Maps the public published-route read endpoint.</summary>
    public static void MapPublishedRouteEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/published")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Published");

        group.MapGet("/{*routeSlug}", HandleRead)
            .WithDisplayName("Read Published Artifact")
            .WithSummary("Resolves a route slug to the active version (or a specific revision via ?version=), enforcing visibility, public-link (?link=&token=), and embed (?embed=true) policy.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .CacheOutput("ContentPublishedRoute");
    }

    private static async Task<IResult> HandleRead(
        string routeSlug,
        [FromServices] IContentPublicationService service,
        [FromServices] IAuditLog auditLog,
        [FromServices] ILogger<PublishedRouteLog> logger,
        HttpContext context,
        [FromQuery] string? version = null,
        [FromQuery] string? link = null,
        [FromQuery] string? token = null,
        [FromQuery] bool? embed = null,
        [FromQuery] string? expand = null)
    {
        using var activity = HonuaTelemetry.StartActivity(ContentPublicationTelemetry.RouteResolve);
        try
        {
            var route = await service.ResolveRouteAsync(routeSlug, context.RequestAborted).ConfigureAwait(false);
            activity?.SetTag(ContentPublicationTelemetry.TagRouteSlug, routeSlug);
            if (route is null || route.Lifecycle != ContentPublicationLifecycle.Active)
            {
                return StandardErrorHelpers.CreateNotFound(context, "Published route not found.");
            }

            activity?.SetTag(ContentPublicationTelemetry.TagPublicationId, route.PublicationId);

            var now = DateTimeOffset.UtcNow;
            var linkProvided = !string.IsNullOrWhiteSpace(link);
            var linkAuthorized = linkProvided
                && ContentPublicLinkVerifier.TryAuthorize(route.Policy.PublicLink, link, token, now);

            if (!linkAuthorized)
            {
                var effectivePolicy = DeriveEffectiveAccessPolicy(route.Policy);
                var accessError = AccessPolicyHelpers.RequireAccess(context, effectivePolicy, null, AccessScope.Read);
                if (accessError is not null)
                {
                    if (linkProvided)
                    {
                        ContentPublicationEndpointsLog.PublicLinkDenied(logger, routeSlug);
                        await ContentPublicationAudit.RecordAsync(
                            auditLog, context, "content-publication.public-link.denied", AuditOutcome.Denied, route.PublicationId,
                            eventType: AuditEventType.Authorization).ConfigureAwait(false);
                    }

                    return accessError;
                }
            }

            if (embed == true && !route.Policy.Embed.AllowEmbedding)
            {
                ContentPublicationEndpointsLog.EmbedDenied(logger, routeSlug);
                await ContentPublicationAudit.RecordAsync(
                    auditLog, context, "content-publication.embed.denied", AuditOutcome.Denied, route.PublicationId,
                    eventType: AuditEventType.Authorization).ConfigureAwait(false);
                return StandardErrorHelpers.CreateForbidden(context, "Embedding is not permitted for this artifact.");
            }

            ContentPublicationVersion? resolved;
            if (!string.IsNullOrWhiteSpace(version))
            {
                using var preview = HonuaTelemetry.StartActivity(ContentPublicationTelemetry.RevisionPreview);
                resolved = await service.GetVersionAsync(route.PublicationId, version, context.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                resolved = await service.GetVersionAsync(route.PublicationId, route.ActiveVersionId, context.RequestAborted).ConfigureAwait(false);
            }

            if (resolved is null)
            {
                return StandardErrorHelpers.CreateNotFound(context, "Published version not found.");
            }

            var includeDependencies = string.Equals(expand, "dependencies", StringComparison.OrdinalIgnoreCase);
            var view = ContentPublicationProjections.ToPublishedView(route, resolved, includeDependencies);

            if (embed == true && route.Policy.Embed.FrameAncestors is { Count: > 0 } frameAncestors)
            {
                context.Response.Headers["Content-Security-Policy"] =
                    "frame-ancestors " + string.Join(' ', frameAncestors);
            }

            activity?.SetTag(ContentPublicationTelemetry.TagRevision, resolved.Revision);
            activity?.SetTag(ContentPublicationTelemetry.TagDependencyCount, resolved.Dependencies.Count);
            return Results.Json(view, ContentPublicationJsonContext.Default.PublishedArtifactView);
        }
        catch (ContentPublicationException ex)
        {
            return MapError(context, ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ContentPublicationEndpointsLog.EndpointFailed(logger, "published.read", ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An internal error occurred while reading the published artifact.");
        }
    }

    /// <summary>
    /// Maps the route policy to an effective <see cref="AccessPolicy"/> for the shared
    /// evaluator: public/anonymous-share routes allow anonymous; otherwise the route's
    /// access policy (or authenticated-only when none) governs.
    /// </summary>
    private static AccessPolicy DeriveEffectiveAccessPolicy(ContentPublicationPolicy policy)
    {
        if (policy.Visibility == ContentPublicationVisibility.Public || policy.Share.AllowAnonymous)
        {
            return new AccessPolicy { AllowAnonymous = true };
        }

        return policy.Access ?? new AccessPolicy();
    }

    private static IResult MapError(HttpContext context, ContentPublicationException ex) => ex.StatusCode switch
    {
        StatusCodes.Status404NotFound => StandardErrorHelpers.CreateNotFound(context, ex.Message),
        StatusCodes.Status409Conflict => StandardErrorHelpers.CreateConflict(context, ex.Message),
        StatusCodes.Status503ServiceUnavailable => StandardErrorHelpers.CreateServiceUnavailable(context, ex.Message),
        _ => StandardErrorHelpers.CreateBadRequest(context, ex.Message),
    };
}
