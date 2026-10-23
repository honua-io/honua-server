// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Raster.CogParser;

/// <summary>Resolves the legacy service-local index stored by a cloud COG registration.</summary>
public static class CogPublicationBinding
{
    /// <summary>
    /// Returns the routable publication a COG registration binds to, or null when the
    /// index is missing or genuinely ambiguous.
    /// </summary>
    /// <remarks>
    /// Registrations carry no service identifier, so this must fail closed on real
    /// collisions. Requiring the layer index to be globally unique across ALL
    /// publications was too strict to ever bind, though: a layer is normally published
    /// through several protocols at once, and this deployment publishes every layer
    /// five ways (image, feature, map, OGC collection, STAC collection). Registration
    /// answered "Layer not found." for every layer in the catalog as a result.
    ///
    /// A COG is raster data read by the ImageServer tile path, so it can only ever back
    /// an image publication - a feature, OGC or STAC publication could not serve it.
    /// Selecting the image publication is therefore a narrowing by the type the feature
    /// actually serves, not a relaxation of the collision rule: two image publications
    /// on the same index still fail closed, and a layer with no image publication keeps
    /// the original unique-publication behaviour.
    /// </remarks>
    public static MetadataV2Publication? Resolve(MetadataV2GraphSnapshot snapshot, int layerIndex)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        MetadataV2Publication? imageMatch = null;
        MetadataV2Publication? anyMatch = null;
        var imageCount = 0;
        var anyCount = 0;
        foreach (var publication in snapshot.Graph.Publications)
        {
            if (publication.LayerIndex != layerIndex || !snapshot.IsRoutable(publication))
            {
                continue;
            }

            anyCount++;
            anyMatch = publication;
            if (publication.PublicationType == MetadataV2PublicationType.EsriImageLayer)
            {
                imageCount++;
                imageMatch = publication;
            }
        }

        if (imageCount > 0)
        {
            return imageCount == 1 ? imageMatch : null;
        }

        return anyCount == 1 ? anyMatch : null;
    }
}
