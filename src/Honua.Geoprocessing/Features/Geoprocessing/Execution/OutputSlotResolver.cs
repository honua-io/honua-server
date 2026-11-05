// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing.Execution;

// Only the first artifact may move, and only when its media type contradicts slot 0, because a guess would file the same job under two kinds.
internal static class OutputSlotResolver
{
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

    // An unrecognized media type stays on positional assignment instead of being guessed.
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
