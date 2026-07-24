// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Honua.Ai.AppGeneration;
using Honua.Ai.MapGeneration;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Publishing.Content.Abstractions;
using Honua.Core.Features.Publishing.Content.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Server.Features.Console;
using Honua.Server.Features.Studio.Export;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Studio.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Studio;

/// <summary>
/// Minimal API endpoints for the Studio package lifecycle.
/// </summary>
internal static class StudioPackageEndpoints
{
    private const string ProblemType = "https://honua.io/problems/studio";

    /// <summary>
    /// Defence-in-depth ceiling on the content-item/draft list page size, mirroring the
    /// Console content list cap (<c>ConsoleContentEndpoints.MaxListLimit</c>).
    /// </summary>
    private const int MaxListLimit = 1_000;

    /// <summary>
    /// Maps Studio package lifecycle endpoints.
    /// </summary>
    public static void MapStudioPackageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // honua-server#3001: RequireStudioLifecycleAuthorization() admits admin unconditionally
        // (unchanged) and, once Studio:EndUserAuthorization:Enabled is on, any authenticated
        // principal -- per-resource ownership and the elevated-operation (publish-request,
        // rollback) operator-grant check are then enforced per handler via
        // IStudioAuthorizationService. With the flag off this is exactly the prior
        // RequireAdminAuthorization() gate (NFR-001).
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/studio")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Studio")
            .RequireStudioLifecycleAuthorization();

        group.MapGet("/package-families", HandleGetPackageFamilies)
            .WithDisplayName("List Studio Package Families")
            .WithSummary("Returns Studio package family capability descriptors for Console authoring.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/package-drafts", HandleCreateDraft)
            .WithDisplayName("Create Studio Package Draft")
            .WithSummary("Creates a mutable Studio package draft.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapGet("/package-drafts", HandleListDrafts)
            .WithDisplayName("List Studio Package Drafts")
            .WithSummary("Lists mutable Studio package drafts with filters (family, workspace, owner) and cursor pagination.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/package-drafts/{draftId:guid}", HandleGetDraft)
            .WithDisplayName("Get Studio Package Draft")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPut("/package-drafts/{draftId:guid}", HandleUpdateDraft)
            .WithDisplayName("Update Studio Package Draft")
            .WithSummary("Replaces a mutable Studio package draft using optimistic generation checks.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapDelete("/package-drafts/{draftId:guid}", HandleDeleteDraft)
            .WithDisplayName("Delete Studio Package Draft")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        group.MapPost("/package-drafts/{draftId:guid}/validate", HandleValidateDraft)
            .WithDisplayName("Validate Studio Package Draft")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPost("/package-drafts/{draftId:guid}/preview-plan", HandlePreviewPlan)
            .WithDisplayName("Create Studio Package Preview Plan")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPost("/package-drafts/{draftId:guid}/content-versions", HandleCreateContentVersion)
            .WithDisplayName("Save Studio Package Draft As Content Version")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapGet("/content-items", HandleListContentItems)
            .WithDisplayName("List Studio Content Items")
            .WithSummary("Lists Studio content items with filters (family, workspace, owner, state), cursor pagination, and publication-registry lifecycle badges.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/content-items/{itemId:guid}/versions", HandleListVersions)
            .WithDisplayName("List Studio Content Versions")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/content-items/{itemId:guid}/versions/{versionId:guid}", HandleGetVersion)
            .WithDisplayName("Get Studio Content Version")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/content-items/{itemId:guid}/version-comparisons", HandleCompareVersions)
            .WithDisplayName("Compare Studio Content Versions")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPost("/content-items/{itemId:guid}/versions/{versionId:guid}/publish-requests", HandleCreatePublishRequest)
            .WithDisplayName("Create Studio Publication Request")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPost("/content-items/{itemId:guid}/versions/{versionId:guid}/reopen", HandleReopenVersion)
            .WithDisplayName("Reopen Studio Content Version")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPost("/content-items/{itemId:guid}/rollback-requests", HandleRollback)
            .WithDisplayName("Create Studio Rollback Request")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPost("/map-packages/generate", HandleGenerateMap)
            .WithDisplayName("Generate Studio Map Package")
            .WithSummary("Generate or refine a map package from a natural-language prompt.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Accepts<GenerateMapPackageRequest>("application/json")
            .Produces<MapGenerationResult>();

        group.MapPost("/app-packages/generate", HandleGenerateApp)
            .WithDisplayName("Generate Studio App Package")
            .WithSummary("Generate or refine a studio-app/v1 app package from a natural-language prompt.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Accepts<GenerateAppPackageRequest>("application/json")
            .Produces<AppGenerationResult>();

        group.MapPost("/{kind}/{id:guid}/export", HandleExportDeliverable)
            .WithDisplayName("Export Studio Deliverable")
            .WithSummary("Render a Studio map, dashboard, or report content item to a shareable PDF or PNG deliverable.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
    }

    private static async Task<IResult> HandleExportDeliverable(
        string kind,
        Guid id,
        [FromServices] IStudioDeliverableExporter exporter,
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] IStudioAuthorizationService authorizationService,
        [FromServices] IAuditLog auditLog,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        if (!TryParseKind(kind, out var family))
        {
            return BadRequest(context, "kind must be one of: map, dashboard, report.");
        }

        if (!TryParseFormat(context.Request.Query["format"], out var format))
        {
            return BadRequest(context, "format must be one of: pdf, png.");
        }

        Guid? versionId = null;
        var versionRaw = context.Request.Query["versionId"].ToString();
        if (!string.IsNullOrWhiteSpace(versionRaw))
        {
            if (!Guid.TryParse(versionRaw, out var parsed))
            {
                return BadRequest(context, "versionId must be a valid GUID.");
            }

            versionId = parsed;
        }

        var store = string.Equals(context.Request.Query["store"], "true", StringComparison.OrdinalIgnoreCase);

        try
        {
            // honua-server#3001: widening the /studio group also widened this export route --
            // rendering by item id with no ownership check would let any authenticated non-admin
            // export another principal's private map/dashboard/report by id. Decision (documented
            // here and in docs/internal/admin-api/studio-package-lifecycle.md#authorization): a
            // non-owner may only ever export the item's published version, never "latest" (which
            // resolves by highest version number and could be newer, unpublished content) and
            // never an explicit non-published versionId -- so the target version is pinned to the
            // published pointer before the exporter ever runs, rather than trusted from the query
            // string or the exporter's own "latest" default.
            var pointers = await service.GetPointersAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (pointers is null)
            {
                return NotFound(context, "Studio content item was not found.");
            }

            var authResult = await EnsureAuthorizedAsync(
                authorizationService, auditLog, timeProvider, context,
                StudioAuthorizationOperation.ReadContentItem, pointers.OwnerId,
                resourceType: "studio-content-item", resourceId: id.ToString("D"),
                isPubliclyReadable: pointers.PublishedVersionId is not null).ConfigureAwait(false);
            if (authResult is not null)
            {
                return authResult;
            }

            if (!IsOwnerOrAdmin(authorizationService, context, pointers.OwnerId))
            {
                if (pointers.PublishedVersionId is not { } publishedVersionId)
                {
                    return Forbidden(
                        context,
                        "The caller does not own this Studio content item and it has no published version.",
                        "studio_authorization/cross_user_denied");
                }

                if (versionId is { } requestedVersionId && requestedVersionId != publishedVersionId)
                {
                    return Forbidden(
                        context,
                        "The caller may only export this Studio content item's published version.",
                        "studio_authorization/cross_user_denied");
                }

                versionId = publishedVersionId;
            }

            var result = await exporter.ExportAsync(
                family,
                id,
                format,
                versionId,
                store,
                context.RequestAborted).ConfigureAwait(false);

            switch (result.Status)
            {
                case StudioDeliverableExportStatus.NotFound:
                    return NotFound(context, result.Detail ?? "Studio content item was not found.");
                case StudioDeliverableExportStatus.KindMismatch:
                    return BadRequest(context, result.Detail ?? "Requested kind does not match the package family.");
            }

            var artifact = result.Artifact!;
            StudioEndpointsLog.DeliverableExported(logger, family, id, artifact.Format, artifact.Content.Length);

            if (store)
            {
                // Storage was requested but persisting the artifact failed (upload or presigned-url
                // lookup returned no URL). Do not report success with a null artifactUrl and no bytes;
                // surface the failure so the caller can retry rather than silently losing the export.
                if (string.IsNullOrEmpty(result.ArtifactUrl))
                {
                    StudioEndpointsLog.EndpointFailed(
                        logger,
                        "deliverable.export",
                        new InvalidOperationException("Studio deliverable was rendered but could not be persisted to share storage."));
                    return ServerError(context, "Studio deliverable was rendered but could not be persisted to share storage.");
                }

                var response = new StudioDeliverableExportResponse
                {
                    ItemId = id,
                    Kind = family,
                    Format = format == StudioDeliverableFormat.Pdf ? "pdf" : "png",
                    FileName = artifact.FileName,
                    ContentType = artifact.ContentType,
                    SizeBytes = artifact.Content.Length,
                    ArtifactUrl = result.ArtifactUrl,
                };
                return Results.Json(
                    ApiResponse<StudioDeliverableExportResponse>.CreateSuccess(response),
                    StudioApiJsonContext.Default.ApiResponseStudioDeliverableExportResponse);
            }

            context.Response.Headers.ContentDisposition = $"attachment; filename=\"{artifact.FileName}\"";
            context.Response.Headers.CacheControl = "no-store";
            return Results.Bytes(artifact.Content, artifact.ContentType, artifact.FileName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(logger, "deliverable.export", ex);
            return ServerError(context, "Studio deliverable could not be exported.");
        }
    }

    private static bool TryParseKind(string kind, out StudioPackageFamily family)
    {
        switch (kind?.ToLowerInvariant())
        {
            case "map":
                family = StudioPackageFamily.Map;
                return true;
            case "dashboard":
                family = StudioPackageFamily.Dashboard;
                return true;
            case "report":
                family = StudioPackageFamily.Report;
                return true;
            default:
                family = default;
                return false;
        }
    }

    private static bool TryParseFormat(string? format, out StudioDeliverableFormat parsed)
    {
        switch (format?.ToLowerInvariant())
        {
            case "pdf":
                parsed = StudioDeliverableFormat.Pdf;
                return true;
            case "png":
            case "":
            case null:
                parsed = StudioDeliverableFormat.Png;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    private static async Task<IResult> HandleGenerateApp(
        HttpContext context,
        [FromServices] IAppGenerationService generation)
    {
        GenerateAppPackageRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                AppGenerationApiJsonContext.Default.GenerateAppPackageRequest,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Prompt))
        {
            var bad = new AppGenerationResult { Status = "error", Rationale = "A non-empty 'prompt' is required." };
            return Results.Json(bad, AppGenerationApiJsonContext.Default.AppGenerationResult, statusCode: StatusCodes.Status400BadRequest);
        }

        var entitlementGate = LicenseGate.RequireEntitlement(
            context,
            FeatureCatalog.AiWorkflowGenerationKey,
            "AI app generation");
        if (entitlementGate is not null)
        {
            return entitlementGate;
        }

        var result = await generation.GenerateAsync(
            new AppGenerationRequest
            {
                Prompt = request.Prompt,
                Provider = request.Provider,
                Model = request.Model,
                CurrentApp = request.Package,
                Conversation = request.Conversation,
                Answers = request.Answers
            },
            context.RequestAborted).ConfigureAwait(false);

        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(result, AppGenerationApiJsonContext.Default.AppGenerationResult);
    }

    private static async Task<IResult> HandleGenerateMap(
        HttpContext context,
        [FromServices] IMapGenerationService generation)
    {
        GenerateMapPackageRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                MapGenerationApiJsonContext.Default.GenerateMapPackageRequest,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Prompt))
        {
            var bad = new MapGenerationResult { Status = "error", Rationale = "A non-empty 'prompt' is required." };
            return Results.Json(bad, MapGenerationApiJsonContext.Default.MapGenerationResult, statusCode: StatusCodes.Status400BadRequest);
        }

        var entitlementGate = LicenseGate.RequireEntitlement(
            context,
            FeatureCatalog.AiWorkflowGenerationKey,
            "AI map generation");
        if (entitlementGate is not null)
        {
            return entitlementGate;
        }

        var result = await generation.GenerateAsync(
            new MapGenerationRequest
            {
                Prompt = request.Prompt,
                Provider = request.Provider,
                Model = request.Model,
                CurrentMap = request.Package,
                Conversation = request.Conversation,
                Answers = request.Answers
            },
            context.RequestAborted).ConfigureAwait(false);

        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(result, MapGenerationApiJsonContext.Default.MapGenerationResult);
    }

    private static IResult HandleGetPackageFamilies(
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger)
    {
        var capabilities = service.GetCapabilities();
        StudioEndpointsLog.CapabilitiesReturned(logger, capabilities.PersistenceMode);
        return Results.Json(
            ApiResponse<StudioPackageFamilyCapabilities>.CreateSuccess(capabilities),
            StudioApiJsonContext.Default.ApiResponseStudioPackageFamilyCapabilities);
    }

    private static async Task<IResult> HandleCreateDraft(
        CreateStudioPackageDraftRequest request,
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] IStudioAuthorizationService authorizationService,
        [FromServices] IAuditLog auditLog,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        if (!TryValidateRequest(request, out var validationError))
        {
            return BadRequest(context, validationError);
        }

        try
        {
            // honua-server#3001: creating a draft under an existing content item must be
            // authorized against that item's recorded owner, not treated as a brand-new
            // (ownerless) resource.
            if (request.ItemId is { } existingItemId)
            {
                var pointers = await service.GetPointersAsync(existingItemId, context.RequestAborted).ConfigureAwait(false);
                if (pointers is not null)
                {
                    var authResult = await EnsureAuthorizedAsync(
                        authorizationService, auditLog, timeProvider, context,
                        StudioAuthorizationOperation.CreateDraft, pointers.OwnerId,
                        resourceType: "studio-content-item", resourceId: existingItemId.ToString("D")).ConfigureAwait(false);
                    if (authResult is not null)
                    {
                        return authResult;
                    }
                }
            }

            var actor = ConsolePrincipal.ResolveActorId(context.User);

            // honua-server#3001: once end-user mode is on, a non-admin caller may only ever
            // own the drafts they create -- ignore any client-supplied ownerId (which would
            // otherwise let a caller assign a draft to someone else) rather than trusting it.
            // CreateStudioPackageDraftCommand.OwnerId falls back to ActorId when null, so
            // omitting it here resolves ownership to the authenticated caller.
            var ownerId = !authorizationService.IsAdmin(context.User) && authorizationService.IsEndUserAuthorizationEnabled
                ? null
                : request.OwnerId;

            var draft = await service.CreateDraftAsync(
                new CreateStudioPackageDraftCommand
                {
                    ItemId = request.ItemId,
                    PackageKey = request.PackageKey,
                    WorkspaceId = request.WorkspaceId,
                    OwnerId = ownerId,
                    Envelope = request.Envelope,
                    ActorId = actor,
                },
                context.RequestAborted).ConfigureAwait(false);

            StudioEndpointsLog.DraftCreated(logger, draft.DraftId, draft.ItemId, draft.Family);
            return Results.Json(
                ApiResponse<StudioPackageDraft>.CreateSuccess(draft),
                StudioApiJsonContext.Default.ApiResponseStudioPackageDraft,
                statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(context, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(logger, "draft.create", ex);
            return ServerError(context, "Studio package draft could not be created.");
        }
    }

    private static async Task<IResult> HandleListDrafts(
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] IStudioAuthorizationService authorizationService,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context,
        [FromQuery] string? family = null,
        [FromQuery] string? workspaceId = null,
        [FromQuery] string? owner = null,
        [FromQuery] string? q = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 25)
    {
        if (!TryParseFamilyList(family, out var families, out var familyError))
        {
            return BadRequest(context, $"family filter is invalid: {familyError}");
        }

        try
        {
            var effectiveOwner = ResolveEffectiveOwnerFilter(authorizationService, context, owner);
            var result = await service.ListDraftsAsync(
                new StudioPackageDraftQuery
                {
                    Families = families,
                    WorkspaceId = NormalizeOptionalQueryValue(workspaceId),
                    OwnerId = NormalizeOptionalQueryValue(effectiveOwner),
                    SearchTerm = NormalizeOptionalQueryValue(q),
                    Cursor = cursor,
                    Limit = ClampListLimit(limit),
                },
                context.RequestAborted).ConfigureAwait(false);

            StudioEndpointsLog.DraftsListed(logger, result.Items.Count, result.Total);
            return Results.Json(
                ApiResponse<StudioPackageDraftListResponse>.CreateSuccess(new StudioPackageDraftListResponse
                {
                    Items = result.Items,
                    Total = result.Total,
                    NextCursor = result.NextCursor,
                }),
                StudioApiJsonContext.Default.ApiResponseStudioPackageDraftListResponse);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(logger, "draft.list", ex);
            return ServerError(context, "Studio package drafts could not be listed.");
        }
    }

    private static async Task<IResult> HandleGetDraft(
        Guid draftId,
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] IStudioAuthorizationService authorizationService,
        [FromServices] IAuditLog auditLog,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        try
        {
            var draft = await service.GetDraftAsync(draftId, context.RequestAborted).ConfigureAwait(false);
            if (draft is null)
            {
                return NotFound(context, "Studio package draft was not found.");
            }

            var authResult = await EnsureAuthorizedAsync(
                authorizationService, auditLog, timeProvider, context,
                StudioAuthorizationOperation.ReadDraft, draft.OwnerId,
                resourceType: "studio-package-draft", resourceId: draftId.ToString("D")).ConfigureAwait(false);
            if (authResult is not null)
            {
                return authResult;
            }

            return Results.Json(
                ApiResponse<StudioPackageDraft>.CreateSuccess(draft),
                StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(logger, "draft.get", ex);
            return ServerError(context, "Studio package draft could not be retrieved.");
        }
    }

    private static async Task<IResult> HandleUpdateDraft(
        Guid draftId,
        UpdateStudioPackageDraftRequest request,
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] IStudioAuthorizationService authorizationService,
        [FromServices] IAuditLog auditLog,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        if (!TryValidateRequest(request, out var validationError))
        {
            return BadRequest(context, validationError);
        }

        try
        {
            var existing = await service.GetDraftAsync(draftId, context.RequestAborted).ConfigureAwait(false);
            if (existing is null)
            {
                return NotFound(context, "Studio package draft was not found.");
            }

            var authResult = await EnsureAuthorizedAsync(
                authorizationService, auditLog, timeProvider, context,
                StudioAuthorizationOperation.UpdateDraft, existing.OwnerId,
                resourceType: "studio-package-draft", resourceId: draftId.ToString("D")).ConfigureAwait(false);
            if (authResult is not null)
            {
                return authResult;
            }

            var isAdmin = authorizationService.IsAdmin(context.User);

            // honua-server#3001: once end-user mode is on, a non-admin caller cannot transfer
            // ownership of a draft they are otherwise authorized to edit -- ignore any
            // client-supplied ownerId (UpdateStudioPackageDraftCommand.OwnerId falls back to the
            // existing owner when null) rather than trusting it.
            var ownerId = !isAdmin && authorizationService.IsEndUserAuthorizationEnabled
                ? null
                : request.OwnerId;

            var draft = await service.UpdateDraftAsync(
                draftId,
                new UpdateStudioPackageDraftCommand
                {
                    PackageKey = request.PackageKey,
                    WorkspaceId = request.WorkspaceId,
                    OwnerId = ownerId,
                    Envelope = request.Envelope,
                    Generation = request.Generation,
                    ActorId = ConsolePrincipal.ResolveActorId(context.User),
                },
                context.RequestAborted).ConfigureAwait(false);

            if (draft is null)
            {
                return NotFound(context, "Studio package draft was not found.");
            }

            StudioEndpointsLog.DraftUpdated(logger, draft.DraftId, draft.Generation);
            return Results.Json(
                ApiResponse<StudioPackageDraft>.CreateSuccess(draft),
                StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(context, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(logger, "draft.update", ex);
            return ServerError(context, "Studio package draft could not be updated.");
        }
    }

    private static async Task<IResult> HandleDeleteDraft(
        Guid draftId,
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] IStudioAuthorizationService authorizationService,
        [FromServices] IAuditLog auditLog,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        try
        {
            var existing = await service.GetDraftAsync(draftId, context.RequestAborted).ConfigureAwait(false);
            if (existing is null)
            {
                return NotFound(context, "Studio package draft was not found.");
            }

            var authResult = await EnsureAuthorizedAsync(
                authorizationService, auditLog, timeProvider, context,
                StudioAuthorizationOperation.DeleteDraft, existing.OwnerId,
                resourceType: "studio-package-draft", resourceId: draftId.ToString("D")).ConfigureAwait(false);
            if (authResult is not null)
            {
                return authResult;
            }

            var deleted = await service.DeleteDraftAsync(draftId, context.RequestAborted).ConfigureAwait(false);
            return deleted
                ? Results.Json(
                    ApiResponse<object>.SuccessWithMessage("Studio package draft deleted."),
                    StudioApiJsonContext.Default.ApiResponseObject)
                : NotFound(context, "Studio package draft was not found.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(logger, "draft.delete", ex);
            return ServerError(context, "Studio package draft could not be deleted.");
        }
    }

    private static async Task<IResult> HandleValidateDraft(
        Guid draftId,
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] IStudioAuthorizationService authorizationService,
        [FromServices] IAuditLog auditLog,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        try
        {
            var existing = await service.GetDraftAsync(draftId, context.RequestAborted).ConfigureAwait(false);
            if (existing is null)
            {
                return NotFound(context, "Studio package draft was not found.");
            }

            var authResult = await EnsureAuthorizedAsync(
                authorizationService, auditLog, timeProvider, context,
                StudioAuthorizationOperation.ValidateDraft, existing.OwnerId,
                resourceType: "studio-package-draft", resourceId: draftId.ToString("D")).ConfigureAwait(false);
            if (authResult is not null)
            {
                return authResult;
            }

            var validation = await service.ValidateDraftAsync(
                draftId,
                ConsolePrincipal.ResolveActorId(context.User),
                context.RequestAborted).ConfigureAwait(false);
            return validation is null
                ? NotFound(context, "Studio package draft was not found.")
                : Results.Json(
                    ApiResponse<StudioValidationSummary>.CreateSuccess(validation),
                    StudioApiJsonContext.Default.ApiResponseStudioValidationSummary);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(logger, "draft.validate", ex);
            return ServerError(context, "Studio package draft could not be validated.");
        }
    }

    private static async Task<IResult> HandlePreviewPlan(
        Guid draftId,
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] IStudioAuthorizationService authorizationService,
        [FromServices] IAuditLog auditLog,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        try
        {
            var existing = await service.GetDraftAsync(draftId, context.RequestAborted).ConfigureAwait(false);
            if (existing is null)
            {
                return NotFound(context, "Studio package draft was not found.");
            }

            var authResult = await EnsureAuthorizedAsync(
                authorizationService, auditLog, timeProvider, context,
                StudioAuthorizationOperation.ValidateDraft, existing.OwnerId,
                resourceType: "studio-package-draft", resourceId: draftId.ToString("D")).ConfigureAwait(false);
            if (authResult is not null)
            {
                return authResult;
            }

            var plan = await service.PreviewPlanAsync(
                draftId,
                ConsolePrincipal.ResolveActorId(context.User),
                context.RequestAborted).ConfigureAwait(false);
            return plan is null
                ? NotFound(context, "Studio package draft was not found.")
                : Results.Json(
                    ApiResponse<StudioPreviewPlan>.CreateSuccess(plan),
                    StudioApiJsonContext.Default.ApiResponseStudioPreviewPlan);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(logger, "draft.preview-plan", ex);
            return ServerError(context, "Studio package preview plan could not be created.");
        }
    }

    private static async Task<IResult> HandleCreateContentVersion(
        Guid draftId,
        SaveStudioContentVersionRequest request,
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] IStudioAuthorizationService authorizationService,
        [FromServices] IAuditLog auditLog,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        if (!TryValidateRequest(request, out var validationError))
        {
            return BadRequest(context, validationError);
        }

        try
        {
            var existing = await service.GetDraftAsync(draftId, context.RequestAborted).ConfigureAwait(false);
            if (existing is null)
            {
                return NotFound(context, "Studio package draft was not found.");
            }

            var authResult = await EnsureAuthorizedAsync(
                authorizationService, auditLog, timeProvider, context,
                StudioAuthorizationOperation.CreateVersion, existing.OwnerId,
                resourceType: "studio-package-draft", resourceId: draftId.ToString("D")).ConfigureAwait(false);
            if (authResult is not null)
            {
                return authResult;
            }

            var version = await service.SaveDraftAsVersionAsync(
                draftId,
                request.ChangeNote,
                ConsolePrincipal.ResolveActorId(context.User),
                context.RequestAborted).ConfigureAwait(false);
            if (version is null)
            {
                return NotFound(context, "Studio package draft was not found.");
            }

            StudioEndpointsLog.VersionCreated(logger, version.ItemId, version.VersionId);
            return Results.Json(
                ApiResponse<StudioContentVersion>.CreateSuccess(version),
                StudioApiJsonContext.Default.ApiResponseStudioContentVersion,
                statusCode: StatusCodes.Status201Created);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(logger, "version.create", ex);
            return ServerError(context, "Studio content version could not be created.");
        }
    }

    private static async Task<IResult> HandleListContentItems(
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] IContentPublicationStore publicationStore,
        [FromServices] IStudioAuthorizationService authorizationService,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context,
        [FromQuery] string? family = null,
        [FromQuery] string? workspaceId = null,
        [FromQuery] string? owner = null,
        [FromQuery] string? state = null,
        [FromQuery] string? q = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 25)
    {
        if (!TryParseFamilyList(family, out var families, out var familyError))
        {
            return BadRequest(context, $"family filter is invalid: {familyError}");
        }
        if (!TryParseStateList(state, out var states, out var stateError))
        {
            return BadRequest(context, $"state filter is invalid: {stateError}");
        }

        try
        {
            var effectiveOwner = ResolveEffectiveOwnerFilter(authorizationService, context, owner);
            var result = await service.ListContentItemsAsync(
                new StudioContentItemQuery
                {
                    Families = families,
                    WorkspaceId = NormalizeOptionalQueryValue(workspaceId),
                    OwnerId = NormalizeOptionalQueryValue(effectiveOwner),
                    States = states,
                    SearchTerm = NormalizeOptionalQueryValue(q),
                    Cursor = cursor,
                    Limit = ClampListLimit(limit),
                },
                context.RequestAborted).ConfigureAwait(false);

            var badges = await ResolvePublicationBadgesAsync(publicationStore, result.Items, context.RequestAborted).ConfigureAwait(false);
            var rows = result.Items
                .Select(item => ToListRow(item, badges.TryGetValue(item.ItemId, out var badge) ? badge : null))
                .ToArray();

            StudioEndpointsLog.ContentItemsListed(logger, rows.Length, result.Total);
            return Results.Json(
                ApiResponse<StudioContentItemListResponse>.CreateSuccess(new StudioContentItemListResponse
                {
                    Items = rows,
                    Total = result.Total,
                    NextCursor = result.NextCursor,
                }),
                StudioApiJsonContext.Default.ApiResponseStudioContentItemListResponse);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(logger, "content-item.list", ex);
            return ServerError(context, "Studio content items could not be listed.");
        }
    }

    private static async Task<IReadOnlyDictionary<Guid, StudioContentItemPublicationBadge>> ResolvePublicationBadgesAsync(
        IContentPublicationStore publicationStore,
        IReadOnlyList<StudioContentItemSummary> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return new Dictionary<Guid, StudioContentItemPublicationBadge>();
        }

        // Convention: Console publishes a Studio content item with
        // sourceContentId == itemId.ToString("D") (see docs/internal/admin-api/
        // content-publication-registry.md and the migration 089 index comment); this is the
        // join key REQ-004 relies on to surface lifecycle badges without one call per row.
        var sourceContentIds = items.Select(item => item.ItemId.ToString("D")).ToArray();
        var routesBySourceContentId = await publicationStore
            .GetLatestRouteStatesBySourceContentIdsAsync(sourceContentIds, cancellationToken)
            .ConfigureAwait(false);

        var badges = new Dictionary<Guid, StudioContentItemPublicationBadge>(items.Count);
        foreach (var item in items)
        {
            if (!routesBySourceContentId.TryGetValue(item.ItemId.ToString("D"), out var route))
            {
                continue;
            }

            badges[item.ItemId] = new StudioContentItemPublicationBadge
            {
                PublicationId = route.PublicationId,
                RouteSlug = route.RouteSlug,
                RoutePath = route.RoutePath,
                Lifecycle = LifecycleToString(route.Lifecycle),
                ActiveRevision = route.ActiveRevision,
                UpdatedAt = route.UpdatedAt,
            };
        }

        return badges;
    }

    private static StudioContentItemListRow ToListRow(StudioContentItemSummary item, StudioContentItemPublicationBadge? publication) => new()
    {
        ItemId = item.ItemId,
        PackageKey = item.PackageKey,
        WorkspaceId = item.WorkspaceId,
        Family = item.Family,
        State = item.State,
        CurrentVersionId = item.CurrentVersionId,
        PublishedVersionId = item.PublishedVersionId,
        CreatedBy = item.CreatedBy,
        UpdatedBy = item.UpdatedBy,
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
        Publication = publication,
    };

    private static string LifecycleToString(ContentPublicationLifecycle lifecycle) => lifecycle switch
    {
        ContentPublicationLifecycle.Active => "active",
        ContentPublicationLifecycle.Suspended => "suspended",
        ContentPublicationLifecycle.Archived => "archived",
        _ => "active",
    };

    /// <summary>
    /// Resolves the <c>owner</c> query filter actually applied to a Studio enumeration query
    /// (honua-server#3001, item 3). Admins (and every caller while the flag is off) keep today's
    /// behavior: the client-supplied <paramref name="requestedOwner"/> is honored as-is (or
    /// omitted, listing every item/draft). Once end-user mode is on, a non-admin caller's
    /// effective owner filter is always forced to their own resolved id -- the list is
    /// server-side scoped to "my content", never trusting a client-supplied <c>owner</c> value
    /// to see another principal's items.
    /// </summary>
    private static string? ResolveEffectiveOwnerFilter(
        IStudioAuthorizationService authorizationService,
        HttpContext context,
        string? requestedOwner)
    {
        if (authorizationService.IsAdmin(context.User) || !authorizationService.IsEndUserAuthorizationEnabled)
        {
            return requestedOwner;
        }

        return authorizationService.ResolveCallerId(context.User);
    }

    /// <summary>
    /// Returns whether the caller is the admin or the resource's recorded owner (honua-server#3001).
    /// Distinct from <see cref="EnsureAuthorizedAsync"/>'s allow/deny outcome: a caller can be
    /// authorized to reach a resource without being its owner (public-read visibility, an
    /// elevated delegate grant), and some responses -- content-version listing, deliverable
    /// export -- must additionally narrow their payload/target in exactly that case.
    /// </summary>
    private static bool IsOwnerOrAdmin(
        IStudioAuthorizationService authorizationService,
        HttpContext context,
        string? resourceOwnerId)
    {
        if (authorizationService.IsAdmin(context.User))
        {
            return true;
        }

        if (resourceOwnerId is null)
        {
            return true;
        }

        var callerId = authorizationService.ResolveCallerId(context.User);
        return string.Equals(resourceOwnerId, callerId, StringComparison.Ordinal);
    }

    private static int ClampListLimit(int requested)
        => Math.Clamp(requested <= 0 ? 25 : requested, 1, MaxListLimit);

    private static string? NormalizeOptionalQueryValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryParseFamilyList(string? value, out IReadOnlyList<StudioPackageFamily>? values, out string error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            values = null;
            error = string.Empty;
            return true;
        }

        var parsed = new List<StudioPackageFamily>();
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseFamily(raw, out var family))
            {
                values = null;
                error = $"unknown value '{raw}'";
                return false;
            }

            parsed.Add(family);
        }

        values = parsed;
        error = string.Empty;
        return true;
    }

    private static bool TryParseStateList(string? value, out IReadOnlyList<StudioContentItemState>? values, out string error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            values = null;
            error = string.Empty;
            return true;
        }

        var parsed = new List<StudioContentItemState>();
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseState(raw, out var state))
            {
                values = null;
                error = $"unknown value '{raw}'";
                return false;
            }

            parsed.Add(state);
        }

        values = parsed;
        error = string.Empty;
        return true;
    }

    private static bool TryParseFamily(string value, out StudioPackageFamily family)
    {
        switch (value.ToLowerInvariant())
        {
            case "query": family = StudioPackageFamily.Query; return true;
            case "analysis": family = StudioPackageFamily.Analysis; return true;
            case "map": family = StudioPackageFamily.Map; return true;
            case "dashboard": family = StudioPackageFamily.Dashboard; return true;
            case "report": family = StudioPackageFamily.Report; return true;
            case "form": family = StudioPackageFamily.Form; return true;
            case "app": family = StudioPackageFamily.App; return true;
            case "workflow": family = StudioPackageFamily.Workflow; return true;
            case "gp": family = StudioPackageFamily.Geoprocessing; return true;
            case "etl": family = StudioPackageFamily.Etl; return true;
            default: family = default; return false;
        }
    }

    private static bool TryParseState(string value, out StudioContentItemState state)
    {
        switch (value.ToLowerInvariant())
        {
            case "draft": state = StudioContentItemState.Draft; return true;
            case "current": state = StudioContentItemState.Current; return true;
            case "published": state = StudioContentItemState.Published; return true;
            default: state = default; return false;
        }
    }

    private static async Task<IResult> HandleListVersions(
        Guid itemId,
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] IStudioAuthorizationService authorizationService,
        [FromServices] IAuditLog auditLog,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        try
        {
            // honua-server#3001: an item with no saved versions has nothing to authorize or
            // hide (the list is empty either way), so the ownership check only runs when the
            // item actually exists.
            var pointers = await service.GetPointersAsync(itemId, context.RequestAborted).ConfigureAwait(false);
            Guid? nonOwnerVisibleVersionId = null;
            if (pointers is not null)
            {
                var authResult = await EnsureAuthorizedAsync(
                    authorizationService, auditLog, timeProvider, context,
                    StudioAuthorizationOperation.ReadContentItem, pointers.OwnerId,
                    resourceType: "studio-content-item", resourceId: itemId.ToString("D"),
                    isPubliclyReadable: pointers.PublishedVersionId is not null).ConfigureAwait(false);
                if (authResult is not null)
                {
                    return authResult;
                }

                // honua-server#3001: a published pointer only admits a non-owner into this
                // endpoint at all (via isPubliclyReadable above) -- it must not expose the
                // item's entire immutable history. Remember the single version a non-owner
                // may see; GetVersion is already scoped identically (isPubliclyReadable
                // requires versionId == PublishedVersionId).
                if (!IsOwnerOrAdmin(authorizationService, context, pointers.OwnerId))
                {
                    nonOwnerVisibleVersionId = pointers.PublishedVersionId;
                }
            }

            var versions = await service.ListVersionsAsync(itemId, context.RequestAborted).ConfigureAwait(false);
            if (nonOwnerVisibleVersionId is { } publishedVersionId)
            {
                versions = versions.Where(v => v.VersionId == publishedVersionId).ToArray();
            }

            return Results.Json(
                ApiResponse<StudioContentVersionListResponse>.CreateSuccess(new StudioContentVersionListResponse
                {
                    ItemId = itemId,
                    Versions = versions,
                }),
                StudioApiJsonContext.Default.ApiResponseStudioContentVersionListResponse);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(logger, "version.list", ex);
            return ServerError(context, "Studio content versions could not be listed.");
        }
    }

    private static async Task<IResult> HandleGetVersion(
        Guid itemId,
        Guid versionId,
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] IStudioAuthorizationService authorizationService,
        [FromServices] IAuditLog auditLog,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        try
        {
            var version = await service.GetVersionAsync(itemId, versionId, context.RequestAborted).ConfigureAwait(false);
            if (version is null)
            {
                return NotFound(context, "Studio content version was not found.");
            }

            var pointers = await service.GetPointersAsync(itemId, context.RequestAborted).ConfigureAwait(false);
            var authResult = await EnsureAuthorizedAsync(
                authorizationService, auditLog, timeProvider, context,
                StudioAuthorizationOperation.ReadContentItem, version.OwnerId,
                resourceType: "studio-content-version", resourceId: versionId.ToString("D"),
                isPubliclyReadable: pointers?.PublishedVersionId == versionId).ConfigureAwait(false);
            if (authResult is not null)
            {
                return authResult;
            }

            return Results.Json(
                ApiResponse<StudioContentVersion>.CreateSuccess(version),
                StudioApiJsonContext.Default.ApiResponseStudioContentVersion);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(logger, "version.get", ex);
            return ServerError(context, "Studio content version could not be retrieved.");
        }
    }

    private static async Task<IResult> HandleCompareVersions(
        Guid itemId,
        CompareStudioContentVersionsRequest request,
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] IStudioAuthorizationService authorizationService,
        [FromServices] IAuditLog auditLog,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        if (!TryValidateRequest(request, out var validationError))
        {
            return BadRequest(context, validationError);
        }

        try
        {
            // Ownership is authorized against the left version; both requested versions belong
            // to the same item id and therefore share the same recorded owner. Comparisons are
            // never treated as publicly readable (honua-server#3001), even when one side is the
            // published version, since the diff itself can expose unpublished draft content.
            var left = await service.GetVersionAsync(itemId, request.LeftVersionId, context.RequestAborted).ConfigureAwait(false);
            if (left is null)
            {
                return NotFound(context, "Studio content version was not found.");
            }

            var authResult = await EnsureAuthorizedAsync(
                authorizationService, auditLog, timeProvider, context,
                StudioAuthorizationOperation.ReadContentItem, left.OwnerId,
                resourceType: "studio-content-item", resourceId: itemId.ToString("D")).ConfigureAwait(false);
            if (authResult is not null)
            {
                return authResult;
            }

            var comparison = await service.CompareVersionsAsync(
                itemId,
                request.LeftVersionId,
                request.RightVersionId,
                context.RequestAborted).ConfigureAwait(false);
            return comparison is null
                ? NotFound(context, "Studio content version was not found.")
                : Results.Json(
                    ApiResponse<StudioVersionComparison>.CreateSuccess(comparison),
                    StudioApiJsonContext.Default.ApiResponseStudioVersionComparison);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(logger, "version.compare", ex);
            return ServerError(context, "Studio content versions could not be compared.");
        }
    }

    private static async Task<IResult> HandleCreatePublishRequest(
        Guid itemId,
        Guid versionId,
        CreateStudioPublicationRequest request,
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] IStudioAuthorizationService authorizationService,
        [FromServices] IAuditLog auditLog,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        if (!TryValidateRequest(request, out var validationError))
        {
            return BadRequest(context, validationError);
        }

        try
        {
            // Elevated operation (REQ-003): ownership alone is not sufficient -- the caller also
            // needs a matching StudioDraft Publish operator grant (own-sentinel or per-item),
            // enforced by IStudioAuthorizationService.
            var targetVersion = await service.GetVersionAsync(itemId, versionId, context.RequestAborted).ConfigureAwait(false);
            if (targetVersion is null)
            {
                return NotFound(context, "Studio content version was not found.");
            }

            var authResult = await EnsureAuthorizedAsync(
                authorizationService, auditLog, timeProvider, context,
                StudioAuthorizationOperation.PublishRequest, targetVersion.OwnerId,
                resourceType: "studio-content-item", resourceId: itemId.ToString("D")).ConfigureAwait(false);
            if (authResult is not null)
            {
                return authResult;
            }

            var publication = await service.CreatePublicationRequestAsync(
                itemId,
                versionId,
                request.Intent,
                request.WarningAcknowledgement,
                ConsolePrincipal.ResolveActorId(context.User),
                context.RequestAborted).ConfigureAwait(false);
            if (publication is null)
            {
                return NotFound(context, "Studio content version was not found.");
            }

            StudioEndpointsLog.PublicationRequestCreated(
                logger,
                publication.RequestId,
                publication.ItemId,
                publication.VersionId,
                publication.Status);
            return Results.Json(
                ApiResponse<StudioPublicationRequest>.CreateSuccess(publication),
                StudioApiJsonContext.Default.ApiResponseStudioPublicationRequest,
                statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(logger, "publish-request.create", ex);
            return ServerError(context, "Studio publication request could not be created.");
        }
    }

    private static async Task<IResult> HandleReopenVersion(
        Guid itemId,
        Guid versionId,
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] IStudioAuthorizationService authorizationService,
        [FromServices] IAuditLog auditLog,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        try
        {
            var targetVersion = await service.GetVersionAsync(itemId, versionId, context.RequestAborted).ConfigureAwait(false);
            if (targetVersion is null)
            {
                return NotFound(context, "Studio content version was not found.");
            }

            var authResult = await EnsureAuthorizedAsync(
                authorizationService, auditLog, timeProvider, context,
                StudioAuthorizationOperation.ReopenVersion, targetVersion.OwnerId,
                resourceType: "studio-content-item", resourceId: itemId.ToString("D")).ConfigureAwait(false);
            if (authResult is not null)
            {
                return authResult;
            }

            var draft = await service.ReopenVersionAsync(
                itemId,
                versionId,
                ConsolePrincipal.ResolveActorId(context.User),
                context.RequestAborted).ConfigureAwait(false);
            if (draft is null)
            {
                return NotFound(context, "Studio content version was not found.");
            }

            StudioEndpointsLog.DraftCreated(logger, draft.DraftId, draft.ItemId, draft.Family);
            return Results.Json(
                ApiResponse<StudioPackageDraft>.CreateSuccess(draft),
                StudioApiJsonContext.Default.ApiResponseStudioPackageDraft,
                statusCode: StatusCodes.Status201Created);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(logger, "version.reopen", ex);
            return ServerError(context, "Studio content version could not be reopened.");
        }
    }

    private static async Task<IResult> HandleRollback(
        Guid itemId,
        CreateStudioRollbackRequest request,
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] IStudioAuthorizationService authorizationService,
        [FromServices] IAuditLog auditLog,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        if (!TryValidateRequest(request, out var validationError))
        {
            return BadRequest(context, validationError);
        }
        if (!StudioPackageEnumHelpers.IsDefined(request.Target))
        {
            return BadRequest(context, "pointer is not supported.");
        }

        try
        {
            // Elevated operation (REQ-003): ownership alone is not sufficient -- the caller also
            // needs a matching StudioDraft Rollback operator grant (own-sentinel or per-item),
            // enforced by IStudioAuthorizationService.
            var targetVersion = await service.GetVersionAsync(itemId, request.TargetVersionId, context.RequestAborted).ConfigureAwait(false);
            if (targetVersion is null)
            {
                return NotFound(context, "Studio content version was not found.");
            }

            var authResult = await EnsureAuthorizedAsync(
                authorizationService, auditLog, timeProvider, context,
                StudioAuthorizationOperation.Rollback, targetVersion.OwnerId,
                resourceType: "studio-content-item", resourceId: itemId.ToString("D")).ConfigureAwait(false);
            if (authResult is not null)
            {
                return authResult;
            }

            var rollback = await service.RollbackAsync(
                itemId,
                request.TargetVersionId,
                request.Target,
                ConsolePrincipal.ResolveActorId(context.User),
                request.Reason,
                context.RequestAborted).ConfigureAwait(false);
            if (rollback is null)
            {
                return NotFound(context, "Studio content version was not found.");
            }

            StudioEndpointsLog.RollbackCreated(
                logger,
                rollback.RequestId,
                rollback.ItemId,
                rollback.Target,
                rollback.TargetVersionId);
            return Results.Json(
                ApiResponse<StudioRollbackRequest>.CreateSuccess(rollback),
                StudioApiJsonContext.Default.ApiResponseStudioRollbackRequest,
                statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(logger, "rollback.create", ex);
            return ServerError(context, "Studio rollback request could not be created.");
        }
    }

    /// <summary>
    /// Authorizes a Studio package-lifecycle operation against the target resource's recorded
    /// owner (honua-server#3001). Returns <see langword="null"/> when authorized (the caller
    /// should proceed); otherwise returns the RFC 7807 problem response to return directly,
    /// having already recorded the denial to the audit log (REQ-003/item 5). Elevated
    /// (operator-grant-gated) allow decisions are also audited.
    /// </summary>
    private static async Task<IResult?> EnsureAuthorizedAsync(
        IStudioAuthorizationService authorizationService,
        IAuditLog auditLog,
        TimeProvider timeProvider,
        HttpContext context,
        StudioAuthorizationOperation operation,
        string? resourceOwnerId,
        string resourceType,
        string? resourceId,
        bool isPubliclyReadable = false)
    {
        var callerId = ConsolePrincipal.ResolveActorId(context.User);
        var decision = await authorizationService.AuthorizeAsync(
            context.User,
            callerId,
            operation,
            resourceOwnerId,
            isPubliclyReadable,
            resourceId,
            context.RequestAborted).ConfigureAwait(false);

        if (decision.IsAllowed)
        {
            if (decision.IsElevated)
            {
                await RecordAuthorizationAuditAsync(
                    auditLog, timeProvider, context, operation, resourceType, resourceId,
                    AuditOutcome.Success, code: null).ConfigureAwait(false);
            }

            return null;
        }

        await RecordAuthorizationAuditAsync(
            auditLog, timeProvider, context, operation, resourceType, resourceId,
            AuditOutcome.Denied, decision.Code).ConfigureAwait(false);

        return Forbidden(context, decision.Reason ?? "The caller is not authorized to perform this operation.", decision.Code ?? "studio_authorization/denied");
    }

    private static Task RecordAuthorizationAuditAsync(
        IAuditLog auditLog,
        TimeProvider timeProvider,
        HttpContext context,
        StudioAuthorizationOperation operation,
        string resourceType,
        string? resourceId,
        AuditOutcome outcome,
        string? code)
    {
        var actor = ConsolePrincipal.ResolveActorId(context.User) ?? AuditEvent.AnonymousActor;
        var auditEvent = new AuditEvent
        {
            Timestamp = timeProvider.GetUtcNow(),
            EventType = AuditEventType.Authorization,
            Actor = actor,
            ActorType = string.Equals(actor, AuditEvent.AnonymousActor, StringComparison.Ordinal)
                ? AuditActorType.Anonymous
                : AuditActorType.UserId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Action = $"studio.{ToSnakeCase(operation)}",
            Outcome = outcome,
            CorrelationId = context.TraceIdentifier,
            Details = code is null ? string.Empty : $"{{\"code\":\"{code}\"}}",
        };

        return auditLog.RecordAsync(auditEvent, context.RequestAborted);
    }

    private static string ToSnakeCase(StudioAuthorizationOperation operation) => operation switch
    {
        StudioAuthorizationOperation.CreateDraft => "create_draft",
        StudioAuthorizationOperation.ReadDraft => "read_draft",
        StudioAuthorizationOperation.UpdateDraft => "update_draft",
        StudioAuthorizationOperation.DeleteDraft => "delete_draft",
        StudioAuthorizationOperation.ValidateDraft => "validate_draft",
        StudioAuthorizationOperation.CreateVersion => "create_version",
        StudioAuthorizationOperation.ListOwn => "list_own",
        StudioAuthorizationOperation.ReadContentItem => "read_content_item",
        StudioAuthorizationOperation.ReopenVersion => "reopen_version",
        StudioAuthorizationOperation.PublishRequest => "publish_request",
        StudioAuthorizationOperation.Rollback => "rollback",
        _ => operation.ToString(),
    };

    private static bool TryValidateRequest<TRequest>(TRequest request, out string error) where TRequest : notnull
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true))
        {
            error = string.Empty;
            return true;
        }

        error = string.Join(", ", results.Select(static result => result.ErrorMessage));
        return false;
    }

    private static IResult BadRequest(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateProblem(context, ProblemType, StatusCodes.Status400BadRequest, "Bad Request", detail);

    /// <summary>
    /// Builds the Studio package-lifecycle authorization-denied RFC 7807 problem, extended with
    /// a machine-readable <c>code</c> member (honua-server#3001, REQ-004) the SDK client can
    /// branch on (for example <c>studio_authorization/cross_user_denied</c>).
    /// </summary>
    private static IResult Forbidden(HttpContext context, string detail, string code)
        => ProblemDetailsHelpers.CreateProblem(context, ProblemType, StatusCodes.Status403Forbidden, "Forbidden", detail, code);

    private static IResult NotFound(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateProblem(context, ProblemType, StatusCodes.Status404NotFound, "Not Found", detail);

    private static IResult Conflict(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateProblem(context, ProblemType, StatusCodes.Status409Conflict, "Conflict", detail);

    private static IResult ServerError(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateProblem(context, ProblemType, StatusCodes.Status500InternalServerError, "Internal Server Error", detail);

    internal sealed class StudioPackageEndpointsMarker;
}
