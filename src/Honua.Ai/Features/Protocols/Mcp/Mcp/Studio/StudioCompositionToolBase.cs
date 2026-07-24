// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.Studio;

/// <summary>
/// Shared invocation shape for the six composition-mutation MCP tools
/// (honua-server#3002, REQ-002: "a small, bounded set of mutation tools for
/// map/app-family drafts ... each patching the draft envelope through the
/// lifecycle service with generation checking"). Every tool: authorizes,
/// loads the draft, verifies it is a composition-eligible family (map/app —
/// <see cref="StudioCompositionBodyEditor.EnsureCompositionEligibleFamily"/>),
/// applies exactly one pure <see cref="StudioCompositionBodyEditor"/>
/// operation to the parsed <see cref="StudioCompositionBody"/>, writes the
/// mutated body back onto the envelope, and pushes it through
/// <see cref="StudioDraftToolBase.ApplyUpdateAsync"/> with the caller-supplied
/// generation. No lifecycle logic (generation checking, validation,
/// persistence) is duplicated — it all flows through
/// <see cref="IStudioPackageLifecycleService"/>.
/// </summary>
internal abstract class StudioCompositionToolBase : StudioDraftToolBase
{
    protected StudioCompositionToolBase(
        IStudioPackageLifecycleService lifecycleService,
        IGeoprocessingJobService jobService,
        ILogger logger)
        : base(lifecycleService, jobService, logger)
    {
    }

    /// <summary>
    /// Loads the draft (verifying it exists and is a composition-eligible
    /// map/app family), applies <paramref name="mutate"/> to its parsed
    /// composition body, writes the result back onto the envelope, and
    /// persists it through the lifecycle service with the caller-supplied
    /// expected generation — the full load→mutate→save round trip every
    /// composition tool needs, plus the audit record (NFR-001) and typed
    /// error translation for every failure mode
    /// (<see cref="TranslateCompositionError"/>).
    /// </summary>
    protected async Task<StudioPackageDraft> MutateCompositionAsync(
        ClaimsPrincipal principal,
        string toolName,
        Guid draftId,
        long expectedGeneration,
        Func<StudioCompositionBody, StudioCompositionBody> mutate,
        CancellationToken cancellationToken)
    {
        try
        {
            var draft = await RequireDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
            StudioCompositionBodyEditor.EnsureCompositionEligibleFamily(draft.Family);

            var body = StudioCompositionBodyEditor.ReadBody(draft.Envelope);
            var mutatedBody = mutate(body);
            var envelope = StudioCompositionBodyEditor.WriteBody(draft.Envelope, mutatedBody);

            var actorId = ActorIdFor(principal);
            var updated = await ApplyUpdateAsync(
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

    /// <summary>
    /// Translates the pure editor's untyped exceptions
    /// (<see cref="StudioCompositionFamilyException"/>,
    /// <see cref="StudioCompositionNotFoundException"/>,
    /// <see cref="StudioCompositionConflictException"/>,
    /// <see cref="StudioCompositionBodyException"/>) into the typed MCP error
    /// channel so a bad layer/widget id or an ineligible family surfaces as a
    /// structured <c>invalid_argument</c>/<c>not_found</c> error instead of an
    /// opaque internal error.
    /// </summary>
    protected static Exception TranslateCompositionError(Exception exception) => exception switch
    {
        StudioCompositionFamilyException or StudioCompositionConflictException or StudioCompositionBodyException =>
            new GeoprocessingValidationException(exception.Message),
        StudioCompositionNotFoundException => new GeoprocessingNotFoundException(exception.Message),
        _ => exception,
    };
}
