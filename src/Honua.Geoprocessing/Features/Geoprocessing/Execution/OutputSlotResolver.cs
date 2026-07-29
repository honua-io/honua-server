// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Single source of truth for mapping a published artifact onto the declared
/// output slot it belongs to.
/// </summary>
/// <remarks>
/// Slot assignment is normally positional: the Nth published artifact belongs to
/// the Nth declared output. That breaks for a process advertising MUTUALLY
/// EXCLUSIVE shapes — <c>imagery.classify</c> declares
/// <c>[Raster, FeatureLayer]</c> but emits exactly one — where the single
/// artifact would always take slot 0 and a GeoJSON result would be recorded as
/// the raster output.
///
/// <para>
/// Two independent code paths make this decision and must agree, or the same job
/// is filed under one kind in the durable workspace ledger and another in the
/// result package: <c>WorkspaceRoutingJobExecutionContext</c> (at publish time)
/// and <c>GeoprocessingResultPackageFactory</c> (afterwards). Hence one shared
/// rule rather than two copies.
/// </para>
///
/// <para>
/// The rule is deliberately conservative, because it runs for every process:
/// only the FIRST published artifact may be remapped, only when its own media
/// type contradicts the kind declared at slot 0, and only when the contradicting
/// kind is itself declared elsewhere. Processes whose outputs are produced
/// together — the <c>[FeatureLayer, Table]</c> analytics ops — are unaffected,
/// since their first artifact matches slot 0 and later artifacts are never
/// remapped.
/// </para>
/// </remarks>
internal static class OutputSlotResolver
{
    /// <summary>
    /// Attempts to remap a published artifact onto the declared output slot its
    /// media type indicates. Returns false to leave positional assignment alone.
    /// </summary>
    /// <param name="artifactReference">The published artifact reference (a data URI for managed executors).</param>
    /// <param name="publishIndex">Zero-based publication order of this artifact.</param>
    /// <param name="declaredKinds">The process's declared output artifact kinds.</param>
    /// <param name="slotIndex">The declared slot the artifact belongs to.</param>
    /// <param name="kind">The artifact kind for that slot.</param>
    public static bool TryResolveAlternativeSlot(
        string? artifactReference,
        int publishIndex,
        IReadOnlyList<ArtifactKind> declaredKinds,
        out int slotIndex,
        out ArtifactKind kind)
    {
        slotIndex = publishIndex;
        kind = ArtifactKind.File;

        if (publishIndex != 0 || declaredKinds is null || declaredKinds.Count < 2)
        {
            return false;
        }

        if (!TryInferKind(artifactReference, out var inferred) || declaredKinds[0] == inferred)
        {
            return false;
        }

        for (var i = 1; i < declaredKinds.Count; i++)
        {
            if (declaredKinds[i] != inferred)
            {
                continue;
            }

            slotIndex = i;
            kind = inferred;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Infers an artifact kind from a data-URI media type. Returns false for
    /// anything it cannot classify confidently, so ambiguity always falls back to
    /// positional assignment rather than a guess.
    /// </summary>
    public static bool TryInferKind(string? artifactReference, out ArtifactKind kind)
    {
        kind = ArtifactKind.File;

        if (string.IsNullOrWhiteSpace(artifactReference)
            || !artifactReference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (artifactReference.StartsWith("data:application/geo+json", StringComparison.OrdinalIgnoreCase))
        {
            kind = ArtifactKind.FeatureLayer;
            return true;
        }

        if (artifactReference.StartsWith("data:image/tiff", StringComparison.OrdinalIgnoreCase))
        {
            kind = ArtifactKind.Raster;
            return true;
        }

        return false;
    }
}
