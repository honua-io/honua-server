// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Server.Features.Styling;

/// <summary>Canonical conditional style edit used by human HTTP and sealed governance replay.</summary>
internal sealed class SharedStyleEditor(IStyleCatalog catalog, StyleEditImpactService impacts,
    IHttpContextAccessor accessor, IServiceProvider services, ILogger<SharedStyleEditor> logger) : ISharedStyleEditor
{
    public async Task<OgcStyleUpdateResult> UpdateHumanAsync(string styleId, int expectedVersion, string expectedImpactHash,
        string document, bool drawingInfo, CancellationToken cancellationToken = default)
    {
        var context = accessor.HttpContext;
        if (context is null || context.User.HasClaim("permission", "agent:workspace"))
            return new(OgcStyleUpdateStatus.Forbidden, "Shared style agent edits require a canonical human-approved proposal.");
        var current = await impacts.ResolveAsync(styleId, cancellationToken).ConfigureAwait(false);
        if (current is null) return new(OgcStyleUpdateStatus.NotFound, "Style not found.");
        if (!current.Impact.CanEdit) return new(OgcStyleUpdateStatus.Forbidden, "The complete shared style scope is not writable.");
        if (current.Impact.StyleVersion != expectedVersion || current.Impact.ImpactHash != expectedImpactHash)
            return new(OgcStyleUpdateStatus.PreconditionFailed, "The shared style or its impact changed; reload before saving.");
        var (candidate, error) = await SharedStyleCandidatePreparation.PrepareAsync(current, document, drawingInfo, context, cancellationToken).ConfigureAwait(false);
        if (error is not null) return error;
        var actor = CanonicalSecurityActor.Resolve(context.User)?.ActorId;
        if (actor is null) return new(OgcStyleUpdateStatus.Forbidden, "An authenticated operator is required.");
        try
        {
            return await ApplyReviewedAsync(current, candidate!, actor, async token =>
            {
                var fresh = await impacts.ResolveAsync(styleId, token).ConfigureAwait(false);
                RequireUnchanged(current, fresh);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Honua.Geoprocessing.GeoprocessingPreconditionFailedException)
        {
            return new(OgcStyleUpdateStatus.PreconditionFailed, "The shared style or its current authority changed; review it again.");
        }
        catch (Honua.Geoprocessing.GeoprocessingAuthorizationException)
        {
            return new(OgcStyleUpdateStatus.Forbidden, "The complete shared style scope is not writable.");
        }
    }

    internal static void RequireUnchanged(ResolvedStyleEdit expected, ResolvedStyleEdit? current)
    {
        if (current is null || !current.Impact.CanEdit || current.Impact.StyleId != expected.Impact.StyleId ||
            current.Impact.StyleVersion != expected.Impact.StyleVersion || current.Impact.ContentHash != expected.Impact.ContentHash ||
            current.Impact.ImpactHash != expected.Impact.ImpactHash || current.Impact.CanonicalLayerId != expected.Impact.CanonicalLayerId ||
            current.PrimaryResource?.Metadata.Id != expected.PrimaryResource?.Metadata.Id)
            throw new Honua.Geoprocessing.GeoprocessingPreconditionFailedException("The shared style, impact or current authority changed; review it again.");
    }

    internal async Task<OgcStyleUpdateResult> ApplyReviewedAsync(ResolvedStyleEdit expected, SharedStyleCandidate candidate,
        string actor, Func<CancellationToken, Task> revalidate, CancellationToken cancellationToken)
    {
        var updated = await catalog.ApplyEditIfSnapshotAsync(expected.StoreSnapshot, candidate.MapLibreJson,
            candidate.DrawingInfoJson, actor, revalidate, cancellationToken).ConfigureAwait(false);
        if (updated is null) return new(OgcStyleUpdateStatus.PreconditionFailed, "The reviewed shared style changed; reload before saving.");
        await RefreshAsync(updated).ConfigureAwait(false);
        return new(OgcStyleUpdateStatus.Updated, null, StyleVersion: updated.LayerStyle?.StyleVersion ?? updated.CatalogStyle!.StyleVersion);
    }

    internal async Task<StyleMutationReceipt> ApplyReviewedOnceAsync(SharedStyleApprovedReplay replay, CancellationToken cancellationToken)
    {
        if (replay.Receipt is { } receipt)
        {
            await replay.RevalidateReceiptAsync(cancellationToken).ConfigureAwait(false);
            // Idempotent refresh repairs a crash after SQL commit but before cache/graph projection.
            // Refresh reads current canonical bytes; the historical candidate is never reapplied.
            await RefreshAsync(replay.Expected.StoreSnapshot).ConfigureAwait(false);
            return receipt;
        }
        var outcome = await catalog.ApplyEditOnceAsync(replay.Expected.StoreSnapshot,
            replay.Payload.Candidate.MapLibreJson, replay.Payload.Candidate.DrawingInfoJson,
            replay.Payload.ActorId, replay.Identity, replay.RevalidateAsync, replay.RevalidateReceiptAsync,
            cancellationToken).ConfigureAwait(false);
        if (outcome is null)
            throw new Honua.Geoprocessing.GeoprocessingPreconditionFailedException("The reviewed shared style changed; submit a new proposal.");
        await RefreshAsync(outcome.UpdatedSnapshot ?? replay.Expected.StoreSnapshot).ConfigureAwait(false);
        return outcome.Receipt;
    }

    private async Task RefreshAsync(StyleEditStoreSnapshot updated)
    {
        var affected = updated.Associations.Select(item => item.LayerId)
            .Concat(updated.LayerStyle is { } layer ? [layer.LayerId] : []).Distinct();
        foreach (var layerId in affected)
        {
            try
            {
                if (services.GetService<ILayerStyleCatalog>() is { } layers)
                    await layers.InvalidateStyleCacheAsync(layerId, CancellationToken.None).ConfigureAwait(false);
                if (services.GetService<IMetadataV2StyleGraphSync>() is { } sync)
                    await sync.SyncLayerStylesAsync(layerId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Canonical bytes are committed. A cache/projection failure cannot turn a
                // successful edit into an apparent failure and encourage duplicate writes.
                logger.LogWarning(exception, "Shared style projection refresh failed for layer {LayerId}", layerId);
            }
        }
        try
        {
            if (services.GetService<IOutputCacheStore>() is { } cache)
                await cache.EvictByTagAsync("ogc-styles", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(exception, "Shared style output cache refresh failed");
        }
    }
}
