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

    protected sealed record CompositionEdit(
        HttpContext HttpContext,
        ClaimsPrincipal Principal,
        IStudioPackageLifecycleService LifecycleService,
        string ToolName,
        Guid DraftId,
        long ExpectedGeneration);

    protected Task<StudioPackageDraft> MutateCompositionAsync(
        CompositionEdit edit,
        Func<StudioCompositionBody, StudioCompositionBody> mutate,
        CancellationToken cancellationToken) =>
        MutateEnvelopeAsync(edit, envelope => StudioCompositionBodyEditor.EditBody(envelope, mutate), cancellationToken);

    protected async Task<StudioPackageDraft> MutateEnvelopeAsync(
        CompositionEdit edit,
        Func<StudioPackageEnvelope, StudioPackageEnvelope> mutate,
        CancellationToken cancellationToken)
    {
        try
        {
            var draft = await RequireAuthorizedDraftAsync(
                edit.HttpContext,
                edit.Principal,
                edit.LifecycleService,
                edit.DraftId,
                StudioAuthorizationOperation.UpdateDraft,
                OperatorOperation.Create,
                cancellationToken).ConfigureAwait(false);
            RequireAuthorizedGeneration(draft, edit.ExpectedGeneration);
            StudioCompositionBodyEditor.EnsureCompositionEligibleFamily(draft.Family);

            var envelope = mutate(draft.Envelope);

            var actorId = ActorIdFor(RequireAuthorizationService(edit.HttpContext), edit.Principal);
            var updated = await ApplyUpdateAsync(
                edit.HttpContext,
                edit.Principal,
                edit.DraftId,
                EnvelopeOnlyUpdate(draft, envelope, edit.ExpectedGeneration, actorId),
                cancellationToken).ConfigureAwait(false);

            Audit(edit.Principal, edit.ToolName, edit.DraftId, generationBefore: edit.ExpectedGeneration, generationAfter: updated.Generation);
            return updated;
        }
        catch (Exception ex) when (ex is StudioCompositionFamilyException
            or StudioCompositionConflictException
            or StudioCompositionNotFoundException
            or StudioCompositionBodyException
            or StudioCompositionOutOfBoundsException)
        {
            throw TranslateCompositionError(ex);
        }
    }

    protected static Exception TranslateCompositionError(Exception exception) => exception switch
    {
        StudioCompositionFamilyException or StudioCompositionConflictException or StudioCompositionBodyException =>
            new GeoprocessingValidationException(exception.Message),
        StudioCompositionOutOfBoundsException =>
            new GeoprocessingValidationException(exception.Message, StudioCompositionOutOfBoundsException.ErrorCode),
        StudioCompositionNotFoundException => new GeoprocessingNotFoundException(exception.Message),
        _ => exception,
    };
}
