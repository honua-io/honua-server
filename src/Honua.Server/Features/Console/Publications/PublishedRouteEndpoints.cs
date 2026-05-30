// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Publishing.Content;
using Honua.Core.Features.Publishing.Content.Abstractions;
using Honua.Core.Features.Publishing.Content.Domain;
using Honua.Core.Features.Publishing.Content.Services;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
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
    private const string OriginHeaderName = "Origin";
    private const string RefererHeaderName = "Referer";
    private const string NullOrigin = "null";

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

            ContentPublicationVersion? activeVersion = null;

            if (!linkAuthorized)
            {
                if (RequiresOwnerGate(route.Policy))
                {
                    activeVersion = await service.GetVersionAsync(route.PublicationId, route.ActiveVersionId, context.RequestAborted).ConfigureAwait(false);
                }

                var accessError = RequireRouteAccess(context, route.Policy, activeVersion);
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

            if (embed == true && !IsEmbedRequestAuthorized(context.Request, route.Policy.Embed))
            {
                ContentPublicationEndpointsLog.EmbedDenied(logger, routeSlug);
                await ContentPublicationAudit.RecordAsync(
                    auditLog, context, "content-publication.embed.denied", AuditOutcome.Denied, route.PublicationId,
                    eventType: AuditEventType.Authorization).ConfigureAwait(false);
                return StandardErrorHelpers.CreateForbidden(context, "Embedding is not permitted for this artifact.");
            }

            ContentPublicationVersion? resolved = activeVersion;
            if (!string.IsNullOrWhiteSpace(version))
            {
                using var preview = HonuaTelemetry.StartActivity(ContentPublicationTelemetry.RevisionPreview);
                resolved = await service.GetVersionAsync(route.PublicationId, version, context.RequestAborted).ConfigureAwait(false);
            }
            else if (resolved is null)
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

    private static IResult? RequireRouteAccess(
        HttpContext context,
        ContentPublicationPolicy policy,
        ContentPublicationVersion? activeVersion)
    {
        if (policy.Visibility == ContentPublicationVisibility.Public || policy.Share.AllowAnonymous)
        {
            return null;
        }

        if (IsPublicationOwner(context, activeVersion))
        {
            return null;
        }

        if (policy.Visibility == ContentPublicationVisibility.Organization)
        {
            return AccessPolicyHelpers.RequireAccess(context, policy.Access ?? new AccessPolicy(), null, AccessScope.Read);
        }

        if (policy.Access is { } access && HasExplicitReadGrant(access))
        {
            return AccessPolicyHelpers.RequireAccess(context, access, null, AccessScope.Read);
        }

        return context.User.Identity?.IsAuthenticated == true
            ? StandardErrorHelpers.CreateForbidden(context, AccessPolicyHelpers.AccessForbiddenMessage)
            : StandardErrorHelpers.CreateUnauthorized(context, AccessPolicyHelpers.AuthRequiredMessage);
    }

    private static bool IsPublicationOwner(HttpContext context, ContentPublicationVersion? activeVersion)
    {
        if (activeVersion is null)
        {
            return false;
        }

        var actor = ConsolePrincipal.ResolveActorId(context.User);
        return !string.IsNullOrWhiteSpace(actor)
            && string.Equals(actor, activeVersion.CreatedBy, StringComparison.Ordinal);
    }

    private static bool HasExplicitReadGrant(AccessPolicy access)
        => access.AllowAnonymous || access.AllowedRoles is { Length: > 0 };

    private static bool RequiresOwnerGate(ContentPublicationPolicy policy)
        => policy.Visibility is not ContentPublicationVisibility.Public and not ContentPublicationVisibility.Organization
            && !policy.Share.AllowAnonymous;

    private static bool IsEmbedRequestAuthorized(HttpRequest request, ContentEmbedPolicy embed)
    {
        if (!embed.AllowEmbedding)
        {
            return false;
        }

        if (embed.AllowedOrigins is not { Count: > 0 } allowedOrigins)
        {
            return true;
        }

        if (!TryResolveRequestOrigin(request, out var requestOrigin))
        {
            return false;
        }

        foreach (var allowedOrigin in allowedOrigins)
        {
            if (TryNormalizeOrigin(allowedOrigin, out var normalizedAllowed)
                && string.Equals(normalizedAllowed, requestOrigin, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveRequestOrigin(HttpRequest request, out string? origin)
    {
        var originHeader = request.Headers[OriginHeaderName].ToString();
        if (!string.IsNullOrWhiteSpace(originHeader))
        {
            return TryNormalizeOrigin(originHeader, out origin);
        }

        var referer = request.Headers[RefererHeaderName].ToString();
        if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
        {
            origin = NormalizeOrigin(refererUri);
            return true;
        }

        origin = null;
        return false;
    }

    private static bool TryNormalizeOrigin(string? value, out string? origin)
    {
        origin = null;
        var candidate = value?.Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        if (string.Equals(candidate, NullOrigin, StringComparison.OrdinalIgnoreCase))
        {
            origin = NullOrigin;
            return true;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        origin = NormalizeOrigin(uri);
        return true;
    }

    private static string NormalizeOrigin(Uri uri)
    {
        var builder = new UriBuilder(uri.Scheme.ToLowerInvariant(), uri.Host.ToLowerInvariant());
        if (!uri.IsDefaultPort)
        {
            builder.Port = uri.Port;
        }

        return builder.Uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    }

    private static IResult MapError(HttpContext context, ContentPublicationException ex) => ex.StatusCode switch
    {
        StatusCodes.Status404NotFound => StandardErrorHelpers.CreateNotFound(context, ex.Message),
        StatusCodes.Status409Conflict => StandardErrorHelpers.CreateConflict(context, ex.Message),
        StatusCodes.Status503ServiceUnavailable => StandardErrorHelpers.CreateServiceUnavailable(context, ex.Message),
        _ => StandardErrorHelpers.CreateBadRequest(context, ex.Message),
    };
}
