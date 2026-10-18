// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Styling.Abstractions;

namespace Honua.Core.Features.Studio.Services;

/// <summary>Applies the same source and appearance policy to typed and whole-draft updates.</summary>
public static class StudioMapStyleMutationPreparation
{
    /// <summary>
    /// Resolves the resulting composition's authorized sources, reconciles its appearance,
    /// and validates the complete style before any durable mutation is submitted.
    /// </summary>
    public static async Task<StudioCompositionBody> PrepareAsync(
        ClaimsPrincipal principal, StudioCompositionBody before, StudioCompositionBody after,
        IStudioMapStyleSourceAuthorizer authorizer, IMapStyleValidator validator,
        CancellationToken cancellationToken, bool mapLocalSnapshot = false)
    {
        if (before.MapSpec is null && after.MapSpec is null) return after;
        if (after.MapSpec is not { ValueKind: JsonValueKind.Object } proposed)
            throw new StudioCompositionBodyException("An initialized map must retain its style document.");

        // Only remaining sources need access: removing a revoked source must still work.
        // Never use a submitted mapSpec or portable locator as the authorization anchor.
        var projection = await authorizer.AuthorizeAsync(principal, after with { MapSpec = null },
            cancellationToken).ConfigureAwait(false);
        if (projection.MapSpec is not { ValueKind: JsonValueKind.Object } authorizedStyle)
            throw new StudioCompositionBodyException("An authorized map source projection is unavailable.");

        StudioCompositionBody candidate;
        if (before.MapSpec is { ValueKind: JsonValueKind.Object })
        {
            candidate = StudioCompositionBodyEditor.ReconcileMapStyle(before, after, authorizedStyle, preserveSharedLabels: mapLocalSnapshot);
            var compositionOnly = StudioCompositionBodyEditor.ReconcileMapStyle(
                before, after with { MapSpec = before.MapSpec }, authorizedStyle, preserveSharedLabels: mapLocalSnapshot);
            if (!JsonElement.DeepEquals(candidate.MapSpec!.Value, compositionOnly.MapSpec!.Value))
            {
                // A whole-style replacement has the same snapshot semantics as the
                // typed style command. Structural reconciliation alone keeps shared refs.
                candidate = StudioCompositionBodyEditor.SetMapStyle(candidate, candidate.MapSpec!.Value);
            }
        }
        else if (before.MapSpec is null)
        {
            candidate = StudioCompositionBodyEditor.SetMapStyle(projection, proposed);
            // Initial base appearance may differ from the generated defaults.
            // Preserve explicitly selected references and validate them below;
            // a typed snapshot edit already cleared its references before here.
            candidate = candidate with { StyleRefs = after.StyleRefs, Layers = after.Layers };
        }
        else
        {
            throw new StudioCompositionBodyException("The draft's stored map document must be repaired before styling.");
        }

        if (!validator.TryValidate(candidate.MapSpec!.Value, out var error))
            throw new StudioCompositionBodyException(error ?? "The resulting map style is not supported.");
        candidate = candidate with
        {
            SourceBindings = projection.SourceBindings,
            SourceBindingIds = projection.SourceBindingIds
        };
        return await authorizer.AuthorizeStyleReferencesAsync(principal, candidate, cancellationToken).ConfigureAwait(false);
    }
}
