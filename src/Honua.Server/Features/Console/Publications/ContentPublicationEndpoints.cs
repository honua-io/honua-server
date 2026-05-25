// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Publishing.Content;
using Honua.Core.Features.Publishing.Content.Abstractions;
using Honua.Core.Features.Publishing.Content.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Console.Publications;

/// <summary>
/// Authenticated content publication management endpoints. These are thin adapters:
/// they parse, authorize (admin), map to <see cref="IContentPublicationService"/>,
/// audit, invalidate caches, and format results. Validation, route-pointer semantics,
/// and dependency resolution live in the service/store.
/// </summary>
internal static class ContentPublicationEndpoints
{
    private const int MaxVersionsPageSize = 100;

    /// <summary>Log category for content publication management endpoints.</summary>
    internal sealed class ContentPublicationManagementLog;

    /// <summary>Maps the authenticated content publication endpoints.</summary>
    public static void MapContentPublicationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/console/publications")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Console", "Publications")
            .RequireAdminAuthorization();

        group.MapPost("/", HandlePublish)
            .WithDisplayName("Publish Content Artifact")
            .WithSummary("Publishes a new immutable artifact version and claims a server-owned route.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapGet("/{publicationId}", HandleGet)
            .WithDisplayName("Get Content Publication")
            .WithSummary("Returns the server-owned route state plus the publication's immutable versions.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/{publicationId}/versions/{versionSelector}", HandleGetVersion)
            .WithDisplayName("Get Content Publication Version")
            .WithSummary("Returns one immutable version by revision number, 'v{n}', or version id.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/{publicationId}/republish", HandleRepublish)
            .WithDisplayName("Republish Content Artifact")
            .WithSummary("Creates a new immutable version and moves the active route pointer to it.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPost("/{publicationId}/rollback", HandleRollback)
            .WithDisplayName("Roll Back Content Route")
            .WithSummary("Moves the active route pointer to an earlier immutable version and records the rollback pointer.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPatch("/{publicationId}/policy", HandleUpdatePolicy)
            .WithDisplayName("Update Content Publication Policy")
            .WithSummary("Updates server-owned visibility/share/embed/public-link policy and records an audited event.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Patch }));
    }

    private static async Task<IResult> HandlePublish(
        [FromBody] PublishContentRequest? request,
        [FromServices] IContentPublicationService service,
        [FromServices] IAuditLog auditLog,
        [FromServices] ILogger<ContentPublicationManagementLog> logger,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.StartActivity(ContentPublicationTelemetry.Publish);
        try
        {
            if (request is null)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, "Request body is required.");
            }

            var actor = ConsolePrincipal.ResolveActorId(context.User) ?? "system";
            var detail = await service.PublishAsync(request, actor, context.TraceIdentifier, context.RequestAborted).ConfigureAwait(false);

            activity?.SetTag(ContentPublicationTelemetry.TagPublicationId, detail.Route.PublicationId);
            activity?.SetTag(ContentPublicationTelemetry.TagRouteSlug, detail.Route.RouteSlug);
            activity?.SetTag(ContentPublicationTelemetry.TagKind, detail.Route.Kind.ToString());

            await ContentPublicationAudit.RecordAsync(auditLog, context, "content-publication.publish", AuditOutcome.Success, detail.Route.PublicationId).ConfigureAwait(false);
            ContentPublicationEndpointsLog.Published(logger, detail.Route.PublicationId, detail.Route.RouteSlug);
            return Results.Json(detail, ContentPublicationJsonContext.Default.ContentPublicationDetail, statusCode: StatusCodes.Status201Created);
        }
        catch (ContentPublicationException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, ex.StatusCode, ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ContentPublicationEndpointsLog.EndpointFailed(logger, "publications.publish", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError, "An internal error occurred while publishing content.");
        }
    }

    private static async Task<IResult> HandleGet(
        string publicationId,
        [FromServices] IContentPublicationService service,
        [FromServices] ILogger<ContentPublicationManagementLog> logger,
        HttpContext context)
    {
        try
        {
            var detail = await service.GetAsync(publicationId, context.RequestAborted).ConfigureAwait(false);
            return detail is null
                ? ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, "Content publication not found.")
                : Results.Json(detail, ContentPublicationJsonContext.Default.ContentPublicationDetail);
        }
        catch (ContentPublicationException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, ex.StatusCode, ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ContentPublicationEndpointsLog.EndpointFailed(logger, "publications.get", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError, "An internal error occurred while retrieving the content publication.");
        }
    }

    private static async Task<IResult> HandleGetVersion(
        string publicationId,
        string versionSelector,
        [FromServices] IContentPublicationService service,
        [FromServices] ILogger<ContentPublicationManagementLog> logger,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.StartActivity(ContentPublicationTelemetry.RevisionPreview);
        try
        {
            var resolved = await service.GetVersionAsync(publicationId, versionSelector, context.RequestAborted).ConfigureAwait(false);
            if (resolved is null)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, "Content publication version not found.");
            }

            activity?.SetTag(ContentPublicationTelemetry.TagPublicationId, publicationId);
            activity?.SetTag(ContentPublicationTelemetry.TagRevision, resolved.Revision);
            return Results.Json(resolved, ContentPublicationJsonContext.Default.ContentPublicationVersion);
        }
        catch (ContentPublicationException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, ex.StatusCode, ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ContentPublicationEndpointsLog.EndpointFailed(logger, "publications.version", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError, "An internal error occurred while retrieving the version.");
        }
    }

    private static async Task<IResult> HandleRepublish(
        string publicationId,
        [FromBody] RepublishContentRequest? request,
        [FromServices] IContentPublicationService service,
        [FromServices] IAuditLog auditLog,
        [FromServices] ILogger<ContentPublicationManagementLog> logger,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.StartActivity(ContentPublicationTelemetry.Republish);
        try
        {
            if (request is null)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, "Request body is required.");
            }

            var actor = ConsolePrincipal.ResolveActorId(context.User) ?? "system";
            var detail = await service.RepublishAsync(publicationId, request, actor, context.TraceIdentifier, context.RequestAborted).ConfigureAwait(false);

            activity?.SetTag(ContentPublicationTelemetry.TagPublicationId, publicationId);
            activity?.SetTag(ContentPublicationTelemetry.TagRevision, detail.Route.ActiveRevision);
            await InvalidateAsync(context, detail.Route.PublicationId, detail.Route.RouteSlug).ConfigureAwait(false);
            await ContentPublicationAudit.RecordAsync(auditLog, context, "content-publication.republish", AuditOutcome.Success, publicationId).ConfigureAwait(false);
            ContentPublicationEndpointsLog.Republished(logger, publicationId, detail.Route.RouteSlug, detail.Route.ActiveRevision);
            return Results.Json(detail, ContentPublicationJsonContext.Default.ContentPublicationDetail);
        }
        catch (ContentPublicationException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, ex.StatusCode, ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ContentPublicationEndpointsLog.EndpointFailed(logger, "publications.republish", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError, "An internal error occurred while republishing content.");
        }
    }

    private static async Task<IResult> HandleRollback(
        string publicationId,
        [FromBody] RollbackContentRequest? request,
        [FromServices] IContentPublicationService service,
        [FromServices] IAuditLog auditLog,
        [FromServices] ILogger<ContentPublicationManagementLog> logger,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.StartActivity(ContentPublicationTelemetry.Rollback);
        try
        {
            if (request is null)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, "Request body is required.");
            }

            var actor = ConsolePrincipal.ResolveActorId(context.User) ?? "system";
            var detail = await service.RollbackAsync(publicationId, request, actor, context.TraceIdentifier, context.RequestAborted).ConfigureAwait(false);

            activity?.SetTag(ContentPublicationTelemetry.TagPublicationId, publicationId);
            activity?.SetTag(ContentPublicationTelemetry.TagRevision, detail.Route.ActiveRevision);
            await InvalidateAsync(context, detail.Route.PublicationId, detail.Route.RouteSlug).ConfigureAwait(false);
            await ContentPublicationAudit.RecordAsync(auditLog, context, "content-publication.rollback", AuditOutcome.Success, publicationId).ConfigureAwait(false);
            ContentPublicationEndpointsLog.RolledBack(logger, publicationId, detail.Route.RouteSlug, detail.Route.ActiveRevision);
            return Results.Json(detail, ContentPublicationJsonContext.Default.ContentPublicationDetail);
        }
        catch (ContentPublicationException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, ex.StatusCode, ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ContentPublicationEndpointsLog.EndpointFailed(logger, "publications.rollback", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError, "An internal error occurred while rolling back content.");
        }
    }

    private static async Task<IResult> HandleUpdatePolicy(
        string publicationId,
        [FromBody] UpdatePublicationPolicyRequest? request,
        [FromServices] IContentPublicationService service,
        [FromServices] IAuditLog auditLog,
        [FromServices] ILogger<ContentPublicationManagementLog> logger,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.StartActivity(ContentPublicationTelemetry.PolicyUpdate);
        try
        {
            if (request is null)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, "Request body is required.");
            }

            var actor = ConsolePrincipal.ResolveActorId(context.User) ?? "system";
            var result = await service.UpdatePolicyAsync(publicationId, request, actor, context.TraceIdentifier, context.RequestAborted).ConfigureAwait(false);

            activity?.SetTag(ContentPublicationTelemetry.TagPublicationId, publicationId);
            await InvalidateAsync(context, result.Detail.Route.PublicationId, result.Detail.Route.RouteSlug).ConfigureAwait(false);
            await ContentPublicationAudit.RecordAsync(
                auditLog,
                context,
                "content-publication.policy.update",
                AuditOutcome.Success,
                publicationId,
                eventType: AuditEventType.ConfigChange).ConfigureAwait(false);
            ContentPublicationEndpointsLog.PolicyUpdated(logger, publicationId, result.Detail.Route.RouteSlug);

            var response = new ContentPublicationPolicyUpdateResponse
            {
                Route = result.Detail.Route,
                CreatedPublicLinkId = result.CreatedPublicLinkId,
                CreatedPublicLinkToken = result.CreatedPublicLinkToken,
            };
            return Results.Json(response, ContentPublicationJsonContext.Default.ContentPublicationPolicyUpdateResponse);
        }
        catch (ContentPublicationException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, ex.StatusCode, ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ContentPublicationEndpointsLog.EndpointFailed(logger, "publications.policy", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError, "An internal error occurred while updating the policy.");
        }
    }

    private static async Task InvalidateAsync(HttpContext context, string publicationId, string routeSlug)
    {
        var invalidation = context.RequestServices.GetService<OutputCacheInvalidationService>();
        if (invalidation is not null)
        {
            await invalidation.InvalidatePublicationAsync(publicationId, routeSlug, context.RequestAborted).ConfigureAwait(false);
        }
    }
}
