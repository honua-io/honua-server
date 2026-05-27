// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
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
        return await LoadFeatureForResponseCoreAsync(
            featureReader, layerId, layerSrid, objectId, responseCrs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Metadata v2 overload of
    /// <see cref="LoadFeatureForResponseAsync(IFeatureReader, int, LayerDefinition, long, CrsDefinition, CancellationToken)"/>.
    /// Reads the storage SRID from <see cref="MetadataV2SpatialExtensions.ReadSrid"/>,
    /// defaulting to <c>4326</c> when the resource declares no SRID.
    /// </summary>
    public static Task<Feature?> LoadFeatureForResponseAsync(
        IFeatureReader featureReader,
        int layerId,
        MetadataV2Resource resource,
        long objectId,
        CrsDefinition responseCrs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(featureReader);
        ArgumentNullException.ThrowIfNull(resource);

        var resourceSrid = resource.ReadSrid() ?? 4326;
        return LoadFeatureForResponseCoreAsync(
            featureReader, layerId, resourceSrid, objectId, responseCrs, cancellationToken);
    }

    private static async Task<Feature?> LoadFeatureForResponseCoreAsync(
        IFeatureReader featureReader,
        int layerId,
        int storageSrid,
        long objectId,
        CrsDefinition responseCrs,
        CancellationToken cancellationToken)
    {
        if (responseCrs.Srid == storageSrid)
        {
            return await featureReader.GetAsync(layerId, objectId, cancellationToken).ConfigureAwait(false);
        }

        var query = new FeatureQuery
        {
            ObjectIds = ImmutableArray.Create(objectId),
            Limit = 1,
            SpatialReferenceSrid = storageSrid,
            OutputSrid = responseCrs.Srid,
            OutputAxisOrder = responseCrs.AxisOrder
        };

        var result = await featureReader.QueryAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        return result.Items.IsDefaultOrEmpty ? null : result.Items[0];
    }
}
