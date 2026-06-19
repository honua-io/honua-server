// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Honua.Ai.AppGeneration;
using Honua.Ai.MapGeneration;
using Honua.Core.Features.Licensing.Domain;
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
    /// Maps Studio package lifecycle endpoints.
    /// </summary>
    public static void MapStudioPackageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/studio")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Studio")
            .RequireAdminAuthorization();

        group.MapGet("/package-families", HandleGetPackageFamilies)
            .WithDisplayName("List Studio Package Families")
            .WithSummary("Returns Studio package family capability descriptors for Console authoring.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/package-drafts", HandleCreateDraft)
            .WithDisplayName("Create Studio Package Draft")
            .WithSummary("Creates a mutable Studio package draft.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

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
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        if (!TryValidateRequest(request, out var validationError))
        {
            return BadRequest(context, validationError);
        }

        try
        {
            var actor = ConsolePrincipal.ResolveActorId(context.User);
            var draft = await service.CreateDraftAsync(
                new CreateStudioPackageDraftCommand
                {
                    ItemId = request.ItemId,
                    PackageKey = request.PackageKey,
                    WorkspaceId = request.WorkspaceId,
                    OwnerId = request.OwnerId,
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

    private static async Task<IResult> HandleGetDraft(
        Guid draftId,
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        try
        {
            var draft = await service.GetDraftAsync(draftId, context.RequestAborted).ConfigureAwait(false);
            return draft is null
                ? NotFound(context, "Studio package draft was not found.")
                : Results.Json(
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
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        if (!TryValidateRequest(request, out var validationError))
        {
            return BadRequest(context, validationError);
        }

        try
        {
            var draft = await service.UpdateDraftAsync(
                draftId,
                new UpdateStudioPackageDraftCommand
                {
                    PackageKey = request.PackageKey,
                    WorkspaceId = request.WorkspaceId,
                    OwnerId = request.OwnerId,
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
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        try
        {
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
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        try
        {
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
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        try
        {
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
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        if (!TryValidateRequest(request, out var validationError))
        {
            return BadRequest(context, validationError);
        }

        try
        {
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

    private static async Task<IResult> HandleListVersions(
        Guid itemId,
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        try
        {
            var versions = await service.ListVersionsAsync(itemId, context.RequestAborted).ConfigureAwait(false);
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
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        try
        {
            var version = await service.GetVersionAsync(itemId, versionId, context.RequestAborted).ConfigureAwait(false);
            return version is null
                ? NotFound(context, "Studio content version was not found.")
                : Results.Json(
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
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        if (!TryValidateRequest(request, out var validationError))
        {
            return BadRequest(context, validationError);
        }

        try
        {
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
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        if (!TryValidateRequest(request, out var validationError))
        {
            return BadRequest(context, validationError);
        }

        try
        {
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
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
    {
        try
        {
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

    private static IResult NotFound(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateProblem(context, ProblemType, StatusCodes.Status404NotFound, "Not Found", detail);

    private static IResult Conflict(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateProblem(context, ProblemType, StatusCodes.Status409Conflict, "Conflict", detail);

    private static IResult ServerError(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateProblem(context, ProblemType, StatusCodes.Status500InternalServerError, "Internal Server Error", detail);

    internal sealed class StudioPackageEndpointsMarker;
}
