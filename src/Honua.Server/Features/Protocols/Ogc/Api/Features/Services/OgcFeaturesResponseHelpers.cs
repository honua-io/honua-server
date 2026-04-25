// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.Protocols.Ogc.Api.Features.Services;

internal static class OgcFeaturesResponseHelpers
{
    public static async Task<Feature?> LoadFeatureForResponseAsync(
        IFeatureReader featureReader,
        int layerId,
        LayerDefinition layer,
        long objectId,
        CrsDefinition responseCrs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(featureReader);

        var layerSrid = layer.SpatialReference.ToSrid();
        if (responseCrs.Srid == layerSrid)
        {
            return await featureReader.GetAsync(layerId, objectId, cancellationToken).ConfigureAwait(false);
        }

        var query = new FeatureQuery
        {
            ObjectIds = ImmutableArray.Create(objectId),
            Limit = 1,
            SpatialReferenceSrid = layerSrid,
            OutputSrid = responseCrs.Srid,
            OutputAxisOrder = responseCrs.AxisOrder
        };

        var result = await featureReader.QueryAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        return result.Items.IsDefaultOrEmpty ? null : result.Items[0];
    }
}
