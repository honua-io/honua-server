// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Studio.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Studio;

internal static partial class StudioPackageEndpoints
{
    private static Task<StudioCompositionBody> PrepareDraftMapStyleAsync(HttpContext context,
        StudioCompositionBody before, StudioCompositionBody after, bool mapLocalSnapshot = false)
        => StudioMapStyleMutationPreparation.PrepareAsync(context.User, before, after,
            context.RequestServices.GetRequiredService<IStudioMapStyleSourceAuthorizer>(),
            context.RequestServices.GetRequiredService<IMapStyleValidator>(), context.RequestAborted, mapLocalSnapshot);

    private static Task<IResult> HandleUpdateDraftStyle(
        Guid draftId,
        UpdateStudioDraftStyleRequest request,
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] IStudioDraftMutationRuntime mutationRuntime,
        [FromServices] StudioEndpointAuthorization authorization,
        [FromServices] IMapStyleValidator validator,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
        => MutateDraftStyleAsync(draftId, request.Generation, service, mutationRuntime,
            authorization, logger, context, async body =>
            {
                var authorized = await StudioDraftStyleInitializer.InitializeAsync(context, body, context.RequestAborted).ConfigureAwait(false);
                var candidate = StudioCompositionBodyEditor.SetMapStyle(authorized, request.MapSpec);
                if (!validator.TryValidate(request.MapSpec, out var error))
                    throw new StudioCompositionBodyException(error ?? "This map style is not supported.");
                return candidate;
            }, mapLocalSnapshot: true);

    private static Task<IResult> HandleInitializeDraftStyle(
        Guid draftId,
        InitializeStudioDraftStyleRequest request,
        [FromServices] IStudioPackageLifecycleService service,
        [FromServices] IStudioDraftMutationRuntime mutationRuntime,
        [FromServices] StudioEndpointAuthorization authorization,
        [FromServices] ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context)
        => MutateDraftStyleAsync(draftId, request.Generation, service, mutationRuntime,
            authorization, logger, context,
            body => StudioDraftStyleInitializer.InitializeAsync(context, body, context.RequestAborted));

    private static async Task<IResult> MutateDraftStyleAsync(
        Guid draftId, long? generation,
        IStudioPackageLifecycleService service,
        IStudioDraftMutationRuntime mutationRuntime,
        StudioEndpointAuthorization authorization,
        ILogger<StudioPackageEndpointsMarker> logger,
        HttpContext context,
        Func<StudioCompositionBody, Task<StudioCompositionBody>> mutate, bool mapLocalSnapshot = false)
    {
        if (generation is null or < 1)
            return BadRequest(context, "generation must be the current positive draft generation.");

        try
        {
            var existing = await service.GetDraftAsync(draftId, context.RequestAborted).ConfigureAwait(false);
            if (existing is null)
                return await NotFoundAfterAuthorizationAsync(authorization, context,
                    StudioAuthorizationOperation.UpdateDraft, "studio-package-draft", draftId.ToString("D"),
                    "Studio package draft was not found.").ConfigureAwait(false);

            var denied = await EnsureAuthorizedAsync(authorization, context,
                StudioAuthorizationOperation.UpdateDraft, existing.OwnerId, existing.TenantId,
                "studio-package-draft", draftId.ToString("D")).ConfigureAwait(false);
            if (denied is not null) return denied;

            StudioCompositionBodyEditor.EnsureCompositionEligibleFamily(existing.Family);
            var before = StudioCompositionBodyEditor.ReadBody(existing.Envelope);
            var body = await mutate(before).ConfigureAwait(false);
            body = await PrepareDraftMapStyleAsync(context, before, body, mapLocalSnapshot).ConfigureAwait(false);
            var actor = authorization.ResolveCallerId(context.User);
            var receipt = await mutationRuntime.UpdateAsync(draftId, new UpdateStudioPackageDraftCommand
            {
                PackageKey = existing.PackageKey,
                WorkspaceId = existing.WorkspaceId,
                Envelope = StudioCompositionBodyEditor.WriteBody(existing.Envelope, body),
                Generation = generation,
                ActorId = actor
            }, BuildMutationContext(context, actor), context.RequestAborted).ConfigureAwait(false);
            SetOperationHeaders(context, receipt.Operation);
            if (receipt.Operation.Status != OperationHandleStatus.Completed)
                return MutationDecision(context, receipt.Operation);

            var draft = receipt.Value
                ?? throw new InvalidOperationException("Completed style update has no durable draft result.");
            StudioEndpointsLog.DraftUpdated(logger, draft.DraftId, draft.Generation);
            return Results.Json(ApiResponse<StudioPackageDraft>.CreateSuccess(draft),
                StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        }
        catch (CapabilityUnavailableException ex) { return CapabilityUnavailable(context, ex); }
        catch (UnauthorizedAccessException)
        {
            return await DenyAsync(authorization, context, StudioAuthorizationOperation.UpdateDraft,
                "studio-package-draft", draftId.ToString("D"),
                "One or more map sources are unavailable to this session.",
                "studio_source_access_denied").ConfigureAwait(false);
        }
        catch (StudioCompositionBodyException ex) { return BadRequest(context, ex.Message); }
        catch (StudioCompositionFamilyException ex) { return BadRequest(context, ex.Message); }
        catch (ArgumentException ex) { return BadRequest(context, ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(context, ex.Message); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(logger, "draft.style", ex);
            return ServerError(context, "The Studio map style could not be saved.");
        }
    }
}
