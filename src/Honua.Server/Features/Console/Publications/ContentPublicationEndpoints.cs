// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.DashboardGeneration;
using Honua.Ai.ReportGeneration;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Publishing.Content;
using Honua.Core.Features.Publishing.Content.Abstractions;
using Honua.Core.Features.Publishing.Content.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Models;
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

        group.MapPost("/generate", HandleGenerateContent)
            .WithDisplayName("Generate Content Document")
            .WithSummary("Generate or refine a report or dashboard document from a natural-language prompt (dispatched on 'kind').")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
    }

    /// <summary>
    /// Shared NL content-generation endpoint. Dispatches on the request's <c>kind</c> discriminator:
    /// <c>dashboard</c> routes to the dashboard generation service, anything else (including the default
    /// <c>report</c>) routes to the report generation service. Both produce the same wire shape — {status,
    /// document, routeSlug, rationale, clarifications, unmappedRequests, capabilityState, provider, model} —
    /// so the console binds either with the same generation outcome mapper. The body is buffered once so the
    /// <c>kind</c> can be peeked before deserializing into the kind-specific request.
    /// </summary>
    private static async Task<IResult> HandleGenerateContent(
        HttpContext context,
        [FromServices] IReportGenerationService reportGeneration,
        [FromServices] IDashboardGenerationService dashboardGeneration)
    {
        // Buffer the body so the kind discriminator can be peeked before deserializing into the
        // kind-specific request shape (both shapes share the kind/prompt/provider/model/document fields).
        using var buffered = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffered, context.RequestAborted).ConfigureAwait(false);
        buffered.Position = 0;

        var kind = PeekKind(buffered);
        buffered.Position = 0;
        context.Response.Headers.CacheControl = "no-store";

        if (string.Equals(kind, "dashboard", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleGenerateDashboard(context, buffered, dashboardGeneration).ConfigureAwait(false);
        }

        return await HandleGenerateReport(context, buffered, reportGeneration).ConfigureAwait(false);
    }

    /// <summary>Reads only the <c>kind</c> discriminator from a buffered request body; null when absent/invalid.</summary>
    private static string? PeekKind(Stream body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("kind", out var kindElement) &&
                kindElement.ValueKind == JsonValueKind.String)
            {
                return kindElement.GetString();
            }
        }
        catch (JsonException)
        {
            // Fall through; the kind-specific deserialize below reports the malformed body.
        }

        return null;
    }

    private static async Task<IResult> HandleGenerateReport(
        HttpContext context,
        Stream body,
        IReportGenerationService generation)
    {
        GenerateReportContentRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                body,
                ReportGenerationApiJsonContext.Default.GenerateReportContentRequest,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Prompt))
        {
            var bad = new ReportGenerationResult { Status = "error", Rationale = "A non-empty 'prompt' is required." };
            return Results.Json(bad, ReportGenerationApiJsonContext.Default.ReportGenerationResult, statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await generation.GenerateAsync(
            new ReportGenerationRequest
            {
                Prompt = request.Prompt,
                Provider = request.Provider,
                Model = request.Model,
                CurrentDocument = request.Document,
                Conversation = request.Conversation,
                Answers = request.Answers
            },
            context.RequestAborted).ConfigureAwait(false);

        return Results.Json(result, ReportGenerationApiJsonContext.Default.ReportGenerationResult);
    }

    private static async Task<IResult> HandleGenerateDashboard(
        HttpContext context,
        Stream body,
        IDashboardGenerationService generation)
    {
        GenerateDashboardContentRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                body,
                DashboardGenerationApiJsonContext.Default.GenerateDashboardContentRequest,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Prompt))
        {
            var bad = new DashboardGenerationResult { Status = "error", Rationale = "A non-empty 'prompt' is required." };
            return Results.Json(bad, DashboardGenerationApiJsonContext.Default.DashboardGenerationResult, statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await generation.GenerateAsync(
            new DashboardGenerationRequest
            {
                Prompt = request.Prompt,
                Provider = request.Provider,
                Model = request.Model,
                CurrentDocument = request.Document,
                Conversation = request.Conversation,
                Answers = request.Answers
            },
            context.RequestAborted).ConfigureAwait(false);

        return Results.Json(result, DashboardGenerationApiJsonContext.Default.DashboardGenerationResult);
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
            return ToProblem(context, ex);
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
            return ToProblem(context, ex);
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
            return ToProblem(context, ex);
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
            return ToProblem(context, ex);
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
            return ToProblem(context, ex);
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
            return ToProblem(context, ex);
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

    /// <summary>
    /// Maps a <see cref="ContentPublicationException"/> to an RFC-7807 problem result. Validation
    /// failures (a <see cref="ContentPublicationValidationException"/>) are field-addressable, so they
    /// flow through <see cref="ProblemDetailsHelpers.CreateValidationProblem"/> with the shared
    /// <c>errors[]</c> extension the console binds onto report/dashboard inputs; all other publication
    /// errors (404/409/503) keep the flat admin problem shape.
    /// </summary>
    private static IResult ToProblem(HttpContext context, ContentPublicationException exception)
        => exception is ContentPublicationValidationException validation
            ? ProblemDetailsHelpers.CreateValidationProblem(context, validation.StatusCode, validation.Errors, validation.Message)
            : ProblemDetailsHelpers.CreateAdminProblem(context, exception.StatusCode, exception.Message);

    private static async Task InvalidateAsync(HttpContext context, string publicationId, string routeSlug)
    {
        var invalidation = context.RequestServices.GetService<OutputCacheInvalidationService>();
        if (invalidation is not null)
        {
            await invalidation.InvalidatePublicationAsync(publicationId, routeSlug, context.RequestAborted).ConfigureAwait(false);
        }
    }
}
