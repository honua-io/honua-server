// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Honua.Ai.AppGeneration;
using Honua.Ai.MapGeneration;
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
            .RequireAdminAuthorization()
            .Accepts<GenerateMapPackageRequest>("application/json")
            .Produces<MapGenerationResult>();

        group.MapPost("/app-packages/generate", HandleGenerateApp)
            .WithDisplayName("Generate Studio App Package")
            .WithSummary("Generate or refine a studio-app/v1 app package from a natural-language prompt.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .RequireAdminAuthorization()
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
        [FromServices] StudioEndpointAuthorization authorization,
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
                authorization, context,
                StudioAuthorizationOperation.ReadContentItem, pointers.OwnerId,
                resourceType: "studio-content-item", resourceId: id.ToString("D"),
                isPubliclyReadable: pointers.PublishedVersionId is not null).ConfigureAwait(false);
            if (authResult is not null)
            {
                return authResult;
            }

            if (!IsOwnerOrAdmin(authorization, context, pointers.OwnerId))
            {
                if (pointers.PublishedVersionId is not { } publishedVersionId)
                {
                    return await DenyAsync(
                        authorization,
                        context,
                        StudioAuthorizationOperation.ReadContentItem,
                        resourceType: "studio-content-item",
                        resourceId: id.ToString("D"),
                        "The caller does not own this Studio content item and it has no published version.",
                        StudioAuthorizationService.CrossUserDeniedCode).ConfigureAwait(false);
                }

                if (versionId is { } requestedVersionId && requestedVersionId != publishedVersionId)
                {
                    return await DenyAsync(
                        authorization,
                        context,
                        StudioAuthorizationOperation.ReadContentItem,
                        resourceType: "studio-content-item",
                        resourceId: id.ToString("D"),
                        "The caller may only export this Studio content item's published version.",
                        StudioAuthorizationService.CrossUserDeniedCode).ConfigureAwait(false);
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
        [FromServices] StudioEndpointAuthorization authorization,
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
                        authorization, context,
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
            var ownerId = !authorization.IsAdmin(context.User) && authorization.IsEndUserAuthorizationEnabled
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
        [FromServices] StudioEndpointAuthorization authorization,
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
            var scopeDenied = await DenyIfCallerUnresolvedForScopedListingAsync(
                authorization,
                context,
                resourceType: "studio-package-draft").ConfigureAwait(false);
            if (scopeDenied is not null)
            {
                return scopeDenied;
            }

            var effectiveOwner = ResolveEffectiveOwnerFilter(authorization, context, owner);
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
        [FromServices] StudioEndpointAuthorization authorization,
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
                authorization, context,
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
        [FromServices] StudioEndpointAuthorization authorization,
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
                authorization, context,
                StudioAuthorizationOperation.UpdateDraft, existing.OwnerId,
                resourceType: "studio-package-draft", resourceId: draftId.ToString("D")).ConfigureAwait(false);
            if (authResult is not null)
            {
                return authResult;
            }

            var isAdmin = authorization.IsAdmin(context.User);

            // honua-server#3001: once end-user mode is on, a non-admin caller cannot transfer
            // ownership of a draft they are otherwise authorized to edit -- ignore any
            // client-supplied ownerId (UpdateStudioPackageDraftCommand.OwnerId falls back to the
            // existing owner when null) rather than trusting it.
            var ownerId = !isAdmin && authorization.IsEndUserAuthorizationEnabled
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
        [FromServices] StudioEndpointAuthorization authorization,
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
                authorization, context,
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
        [FromServices] StudioEndpointAuthorization authorization,
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
                authorization, context,
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
        [FromServices] StudioEndpointAuthorization authorization,
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
                authorization, context,
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
        [FromServices] StudioEndpointAuthorization authorization,
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
                authorization, context,
                StudioAuthorizationOperation.CreateVersion, existing.OwnerId,
                resourceType: "studio-package-draft", resourceId: draftId.ToString("D")).ConfigureAwait(false);
            if (authResult is not null)
            {
                return authResult;
            }

            // Saving a draft also advances the parent content item's CurrentVersionId pointer.
            // A mixed-owner draft is valid (an admin can create one on another user's behalf),
            // but its draft owner must not be able to mutate a content item owned by somebody
            // else. Authorize both boundaries before invoking the atomic version save.
            var pointers = await service.GetPointersAsync(existing.ItemId, context.RequestAborted).ConfigureAwait(false);
            if (pointers is null)
            {
                return NotFound(context, "Studio content item was not found.");
            }

            var itemAuthResult = await EnsureAuthorizedAsync(
                authorization, context,
                StudioAuthorizationOperation.CreateVersion, pointers.OwnerId,
                resourceType: "studio-content-item", resourceId: existing.ItemId.ToString("D")).ConfigureAwait(false);
            if (itemAuthResult is not null)
            {
                return itemAuthResult;
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
        [FromServices] StudioEndpointAuthorization authorization,
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
            var scopeDenied = await DenyIfCallerUnresolvedForScopedListingAsync(
                authorization,
                context,
                resourceType: "studio-content-item").ConfigureAwait(false);
            if (scopeDenied is not null)
            {
                return scopeDenied;
            }

            var effectiveOwner = ResolveEffectiveOwnerFilter(authorization, context, owner);
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
        OwnerId = item.OwnerId,
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
        StudioEndpointAuthorization authorization,
        HttpContext context,
        string? requestedOwner)
    {
        if (authorization.IsAdmin(context.User) || !authorization.IsEndUserAuthorizationEnabled)
        {
            return requestedOwner;
        }

        return authorization.ResolveCallerId(context.User);
    }

    /// <summary>
    /// Denies a scoped Studio enumeration request (honua-server#3001 follow-up) when end-user
    /// mode is on, the caller is non-admin, and <see cref="StudioEndpointAuthorization.ResolveCallerId"/>
    /// cannot resolve a caller id (for example a principal with none of NameIdentifier, "sub",
    /// the admin API-key id/name claims, or <see cref="System.Security.Claims.ClaimsIdentity.Name"/>).
    /// Without this check, <see cref="ResolveEffectiveOwnerFilter"/> would return null for such a
    /// caller, which downstream <see cref="NormalizeOptionalQueryValue"/> treats as "no owner
    /// filter" -- silently listing every draft/content item instead of scoping to "my content".
    /// Returns the RFC 7807 problem response to return directly, or <see langword="null"/> when
    /// the caller should proceed.
    /// </summary>
    private static async Task<IResult?> DenyIfCallerUnresolvedForScopedListingAsync(
        StudioEndpointAuthorization authorization,
        HttpContext context,
        string resourceType)
    {
        if (authorization.IsAdmin(context.User) || !authorization.IsEndUserAuthorizationEnabled)
        {
            return null;
        }

        var callerId = authorization.ResolveCallerId(context.User);
        if (!string.IsNullOrWhiteSpace(callerId))
        {
            return null;
        }

        return await DenyAsync(
            authorization,
            context,
            StudioAuthorizationOperation.ListOwn,
            resourceType,
            resourceId: null,
            "The caller's identity could not be resolved for this scoped Studio listing request.",
            StudioAuthorizationService.AuthenticationRequiredCode).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns whether the caller is the admin or the resource's recorded owner (honua-server#3001).
    /// Distinct from <see cref="EnsureAuthorizedAsync"/>'s allow/deny outcome: a caller can be
    /// authorized to reach a resource without being its owner (public-read visibility, an
    /// elevated delegate grant), and some responses -- content-version listing, deliverable
    /// export -- must additionally narrow their payload/target in exactly that case. Mirrors
    /// <see cref="Honua.Core.Features.Studio.Services.StudioAuthorizationService"/>'s fail-closed
    /// ownership check: a null <paramref name="resourceOwnerId"/> (an existing resource with no
    /// recorded owner, for example an unbackfilled legacy row) is never treated as owned by the
    /// caller, so a non-admin caller who only reached this point via public-read visibility is
    /// correctly narrowed to the published version rather than granted full owner-equivalent
    /// access.
    /// </summary>
    private static bool IsOwnerOrAdmin(
        StudioEndpointAuthorization authorization,
        HttpContext context,
        string? resourceOwnerId)
    {
        if (authorization.IsAdmin(context.User))
        {
            return true;
        }

        if (resourceOwnerId is null)
        {
            return false;
        }

        var callerId = authorization.ResolveCallerId(context.User);
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
        [FromServices] StudioEndpointAuthorization authorization,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        try
        {
            var pointers = await service.GetPointersAsync(itemId, context.RequestAborted).ConfigureAwait(false);
            var versions = await service.ListVersionsAsync(itemId, context.RequestAborted).ConfigureAwait(false);

            // PR #3018 review, round 6, item 1: version.OwnerId is an immutable snapshot of
            // the draft owner that created that specific version and may differ from the
            // content item's owner. Authorizing once against the item would therefore expose
            // another owner's unpublished version in a mixed-owner history. Apply the exact
            // same owner-or-published check as HandleGetVersion to every returned version.
            // Preserve the previous fail-closed response when no version is visible, while
            // returning only the authorized subset when the history contains a mix.
            var visibleVersions = new List<StudioContentVersion>(versions.Count);
            IResult? firstDenial = null;
            foreach (var version in versions)
            {
                var authResult = await EnsureAuthorizedAsync(
                    authorization, context,
                    StudioAuthorizationOperation.ReadContentItem, version.OwnerId,
                    resourceType: "studio-content-version", resourceId: version.VersionId.ToString("D"),
                    isPubliclyReadable: pointers?.PublishedVersionId == version.VersionId).ConfigureAwait(false);
                if (authResult is null)
                {
                    visibleVersions.Add(version);
                }
                else
                {
                    firstDenial ??= authResult;
                }
            }

            if (visibleVersions.Count == 0 && firstDenial is not null)
            {
                return firstDenial;
            }

            return Results.Json(
                ApiResponse<StudioContentVersionListResponse>.CreateSuccess(new StudioContentVersionListResponse
                {
                    ItemId = itemId,
                    Versions = visibleVersions,
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
        [FromServices] StudioEndpointAuthorization authorization,
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
                authorization, context,
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
        [FromServices] StudioEndpointAuthorization authorization,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        if (!TryValidateRequest(request, out var validationError))
        {
            return BadRequest(context, validationError);
        }

        try
        {
            // PR #3018 review, round 5, item 2: both requested versions must be individually
            // authorized. A version's OwnerId only snapshots who created *that* version (for
            // example the draft owner at save-as-version time) and can diverge from another
            // version under the same item id -- a caller who owns leftVersionId cannot be
            // assumed to also be entitled to read an arbitrary rightVersionId. Comparisons are
            // never treated as publicly readable (honua-server#3001), even when one side is the
            // published version, since the diff itself can expose unpublished draft content.
            var left = await service.GetVersionAsync(itemId, request.LeftVersionId, context.RequestAborted).ConfigureAwait(false);
            if (left is null)
            {
                return NotFound(context, "Studio content version was not found.");
            }

            var right = await service.GetVersionAsync(itemId, request.RightVersionId, context.RequestAborted).ConfigureAwait(false);
            if (right is null)
            {
                return NotFound(context, "Studio content version was not found.");
            }

            var leftAuthResult = await EnsureAuthorizedAsync(
                authorization, context,
                StudioAuthorizationOperation.ReadContentItem, left.OwnerId,
                resourceType: "studio-content-item", resourceId: itemId.ToString("D")).ConfigureAwait(false);
            if (leftAuthResult is not null)
            {
                return leftAuthResult;
            }

            var rightAuthResult = await EnsureAuthorizedAsync(
                authorization, context,
                StudioAuthorizationOperation.ReadContentItem, right.OwnerId,
                resourceType: "studio-content-item", resourceId: itemId.ToString("D")).ConfigureAwait(false);
            if (rightAuthResult is not null)
            {
                return rightAuthResult;
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
        [FromServices] StudioEndpointAuthorization authorization,
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

            // PR #3018 review, round 5, item 1: publish-request moves the ITEM's
            // PublishedVersionId pointer, so authorization must be against the item's immutable
            // owner_id -- not targetVersion.OwnerId, which only snapshots who created that
            // particular version and can diverge from the item's recorded owner (for example a
            // version saved from a draft reopened by someone else). Authorizing on the version's
            // owner would let a caller who merely owns the target version (plus an "own"-sentinel
            // publish grant) move the published pointer of an item someone else actually owns.
            var pointers = await service.GetPointersAsync(itemId, context.RequestAborted).ConfigureAwait(false);
            if (pointers is null)
            {
                return NotFound(context, "Studio content item was not found.");
            }

            var authResult = await EnsureAuthorizedAsync(
                authorization, context,
                StudioAuthorizationOperation.PublishRequest, pointers.OwnerId,
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
        [FromServices] StudioEndpointAuthorization authorization,
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
                authorization, context,
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
        [FromServices] StudioEndpointAuthorization authorization,
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

            // PR #3018 review, round 5, item 1: rollback moves the ITEM's current/published
            // pointer (see request.Target), so authorization must be against the item's
            // immutable owner_id -- not targetVersion.OwnerId, which only snapshots who created
            // that particular version and can diverge from the item's recorded owner. See the
            // identical rationale on HandleCreatePublishRequest.
            var pointers = await service.GetPointersAsync(itemId, context.RequestAborted).ConfigureAwait(false);
            if (pointers is null)
            {
                return NotFound(context, "Studio content item was not found.");
            }

            var authResult = await EnsureAuthorizedAsync(
                authorization, context,
                StudioAuthorizationOperation.Rollback, pointers.OwnerId,
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
        StudioEndpointAuthorization authorization,
        HttpContext context,
        StudioAuthorizationOperation operation,
        string? resourceOwnerId,
        string resourceType,
        string? resourceId,
        bool isPubliclyReadable = false)
    {
        var decision = await authorization.AuthorizeAsync(
            context,
            operation,
            resourceOwnerId,
            resourceType,
            resourceId,
            isPubliclyReadable).ConfigureAwait(false);

        if (decision.IsAllowed)
        {
            return null;
        }

        return Forbidden(context, decision.Reason ?? "The caller is not authorized to perform this operation.", decision.Code ?? "studio_authorization/denied");
    }

    /// <summary>
    /// Records a stable, explicit Studio authorization denial through the shared endpoint audit
    /// seam before returning its RFC 7807 response. Use this for secondary target/scope checks
    /// that are intentionally stricter than the baseline authorization service decision.
    /// </summary>
    private static async Task<IResult> DenyAsync(
        StudioEndpointAuthorization authorization,
        HttpContext context,
        StudioAuthorizationOperation operation,
        string resourceType,
        string? resourceId,
        string reason,
        string code)
    {
        var decision = await authorization.DenyAsync(
            context,
            operation,
            resourceType,
            resourceId,
            code,
            reason).ConfigureAwait(false);
        return Forbidden(
            context,
            decision.Reason ?? reason,
            decision.Code ?? code);
    }

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
