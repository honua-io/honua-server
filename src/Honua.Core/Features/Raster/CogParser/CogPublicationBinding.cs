// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Raster.CogParser;

/// <summary>Resolves the legacy service-local index stored by a cloud COG registration.</summary>
public static class CogPublicationBinding
{
    /// <summary>
    /// Returns the unique routable publication, or null when the index is missing or ambiguous.
    /// Registrations carry no service identifier, so even aliases must fail closed on collisions.
    /// </summary>
    public static MetadataV2Publication? Resolve(MetadataV2GraphSnapshot snapshot, int layerIndex)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        MetadataV2Publication? match = null;
        foreach (var publication in snapshot.Graph.Publications)
        {
            if (publication.LayerIndex != layerIndex || !snapshot.IsRoutable(publication))
            {
                continue;
            }
            if (match is not null)
            {
                return null;
            }
            match = publication;
        }
        return match;
    }
}
