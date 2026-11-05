// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.Studio;

internal abstract class StudioCompositionToolBase : StudioDraftToolBase
{
    protected StudioCompositionToolBase(IGeoprocessingJobService jobService, ILogger logger)
        : base(jobService, logger)
    {
    }

    protected async Task<StudioPackageDraft> MutateCompositionAsync(
        HttpContext httpContext,
        ClaimsPrincipal principal,
        string toolName,
        IStudioPackageLifecycleService lifecycleService,
        Guid draftId,
        long expectedGeneration,
        Func<StudioCompositionBody, StudioCompositionBody> mutate,
        CancellationToken cancellationToken)
    {
        try
        {
            var draft = await RequireAuthorizedDraftAsync(
                httpContext,
                principal,
                lifecycleService,
                draftId,
                StudioAuthorizationOperation.UpdateDraft,
                OperatorOperation.Create,
                cancellationToken).ConfigureAwait(false);
            RequireAuthorizedGeneration(draft, expectedGeneration);
            StudioCompositionBodyEditor.EnsureCompositionEligibleFamily(draft.Family);

            var body = StudioCompositionBodyEditor.ReadBody(draft.Envelope);
            var mutatedBody = mutate(body);
            var envelope = StudioCompositionBodyEditor.WriteBody(draft.Envelope, mutatedBody);

            var actorId = ActorIdFor(RequireAuthorizationService(httpContext), principal);
            var updated = await ApplyUpdateAsync(
                httpContext,
                principal,
                draftId,
                EnvelopeOnlyUpdate(draft, envelope, expectedGeneration, actorId),
                cancellationToken).ConfigureAwait(false);

            Audit(principal, toolName, draftId, generationBefore: expectedGeneration, generationAfter: updated.Generation);
            return updated;
        }
        catch (Exception ex) when (ex is StudioCompositionFamilyException
            or StudioCompositionConflictException
            or StudioCompositionNotFoundException
            or StudioCompositionBodyException)
        {
            throw TranslateCompositionError(ex);
        }
    }

    protected static Exception TranslateCompositionError(Exception exception) => exception switch
    {
        StudioCompositionFamilyException or StudioCompositionConflictException or StudioCompositionBodyException =>
            new GeoprocessingValidationException(exception.Message),
        StudioCompositionNotFoundException => new GeoprocessingNotFoundException(exception.Message),
        _ => exception,
    };
}
