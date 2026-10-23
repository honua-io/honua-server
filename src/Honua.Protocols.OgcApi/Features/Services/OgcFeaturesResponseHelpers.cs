// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Linq;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.Ogc.Api.Features.Services;

internal static class OgcFeaturesResponseHelpers
{
    /// <summary>
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

    /// <summary>
    /// Loads a batch of features for mutation responses with a single
    /// <see cref="FeatureQuery.ObjectIds"/> query (projected to the response CRS when it
    /// differs from storage), keyed by internal object id. Object ids that no longer
    /// resolve are simply absent from the result.
    /// </summary>
    public static async Task<IReadOnlyDictionary<long, Feature>> LoadFeaturesForResponseAsync(
        IFeatureReader featureReader,
        int layerId,
        MetadataV2Resource resource,
        IReadOnlyCollection<long> objectIds,
        CrsDefinition responseCrs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(featureReader);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(objectIds);

        var loaded = new Dictionary<long, Feature>(objectIds.Count);
        if (objectIds.Count == 0)
        {
            return loaded;
        }

        var seen = new HashSet<long>();
        var ids = objectIds.Where(seen.Add).ToImmutableArray();
        var storageSrid = resource.ReadSrid() ?? 4326;
        var query = responseCrs.Srid == storageSrid
            ? new FeatureQuery
            {
                ObjectIds = ids,
                Limit = ids.Length
            }
            : new FeatureQuery
            {
                ObjectIds = ids,
                Limit = ids.Length,
                SpatialReferenceSrid = storageSrid,
                OutputSrid = responseCrs.Srid,
                OutputAxisOrder = responseCrs.AxisOrder
            };

        var result = await featureReader.QueryAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        if (!result.Items.IsDefaultOrEmpty)
        {
            foreach (var feature in result.Items)
            {
                loaded[feature.Id] = feature;
            }
        }

        return loaded;
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

    /// <summary>
    /// Loads a projected feature and the storage snapshot it was projected from.
    /// A second storage read after the projection must match the first. Otherwise the
    /// response body and the entity tag would describe different row versions.
    /// </summary>
    public static Task<ConsistentProjectedFeatureRead> LoadProjectedConsistentWithStorageAsync(
        IFeatureReader featureReader,
        int layerId,
        int storageSrid,
        long objectId,
        CrsDefinition responseCrs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(featureReader);
        return ReadUntilStorageStableAsync(
            token => featureReader.GetAsync(layerId, objectId, token),
            token => LoadFeatureForResponseCoreAsync(
                featureReader, layerId, storageSrid, objectId, responseCrs, token),
            cancellationToken);
    }

    internal static async Task<ConsistentProjectedFeatureRead> ReadUntilStorageStableAsync(
        Func<CancellationToken, Task<Feature?>> readStorage,
        Func<CancellationToken, Task<Feature?>> readProjected,
        CancellationToken cancellationToken,
        int attempts = 3)
    {
        ArgumentNullException.ThrowIfNull(readStorage);
        ArgumentNullException.ThrowIfNull(readProjected);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        var sawFeature = false;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var before = await readStorage(cancellationToken).ConfigureAwait(false);
            if (!before.HasValue)
            {
                return sawFeature
                    ? Unstable()
                    : new ConsistentProjectedFeatureRead(ConsistentFeatureReadStatus.NotFound, null, null);
            }

            sawFeature = true;
            var projected = await readProjected(cancellationToken).ConfigureAwait(false);
            if (!projected.HasValue)
            {
                continue;
            }

            var after = await readStorage(cancellationToken).ConfigureAwait(false);
            if (after.HasValue &&
                FeatureStateToken.Compute(before.Value) == FeatureStateToken.Compute(after.Value))
            {
                return new ConsistentProjectedFeatureRead(
                    ConsistentFeatureReadStatus.Found,
                    projected,
                    before);
            }
        }

        return Unstable();
    }

    private static ConsistentProjectedFeatureRead Unstable()
        => new(ConsistentFeatureReadStatus.Unstable, null, null);
}

/// <summary>Whether a projected read and its storage snapshot describe one row version.</summary>
internal enum ConsistentFeatureReadStatus
{
    Found,
    NotFound,
    Unstable,
}

/// <summary>A projected response feature paired with the storage snapshot hashed into its ETag.</summary>
internal readonly record struct ConsistentProjectedFeatureRead(
    ConsistentFeatureReadStatus Status,
    Feature? Response,
    Feature? Storage);
