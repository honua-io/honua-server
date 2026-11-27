// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Linq;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.Ogc.Api.Features.Services;

internal static class OgcFeaturesResponseHelpers
{
    public static Task<Feature?> LoadFeatureForResponseAsync(
        IFeatureReader featureReader,
        int layerId,
        MetadataV2Object resource,
        long objectId,
        CrsDefinition responseCrs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(featureReader);
        ArgumentNullException.ThrowIfNull(resource);

        return LoadFeatureForResponseCoreAsync(
            new OgcFeatureReadTarget
            {
                FeatureReader = featureReader,
                LayerId = layerId,
                Resource = resource,
                ObjectId = objectId
            },
            responseCrs,
            cancellationToken);
    }

    public static async Task<IReadOnlyDictionary<long, Feature>> LoadFeaturesForResponseAsync(
        IFeatureReader featureReader,
        int layerId,
        MetadataV2Object resource,
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
        var query = BuildBatchResponseQuery(resource, ids, responseCrs);

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

    private static FeatureQuery BuildBatchResponseQuery(
        MetadataV2Object resource,
        ImmutableArray<long> ids,
        CrsDefinition responseCrs)
    {
        var storageSrid = resource.ReadSrid() ?? 4326;
        var publicIdAttributeName = ResolveHiddenPrimaryIdAttributeName(resource);
        return responseCrs.Srid == storageSrid
            ? new FeatureQuery
            {
                ObjectIds = ids,
                Limit = ids.Length,
                PublicIdAttributeName = publicIdAttributeName
            }
            : new FeatureQuery
            {
                ObjectIds = ids,
                Limit = ids.Length,
                PublicIdAttributeName = publicIdAttributeName,
                SpatialReferenceSrid = storageSrid,
                OutputSrid = responseCrs.Srid,
                OutputAxisOrder = responseCrs.AxisOrder
            };
    }

    private static async Task<Feature?> LoadFeatureForResponseCoreAsync(
        OgcFeatureReadTarget target,
        CrsDefinition responseCrs,
        CancellationToken cancellationToken)
    {
        var storageSrid = target.Resource.ReadSrid() ?? 4326;
        var publicIdAttributeName = ResolveHiddenPrimaryIdAttributeName(target.Resource);
        if (responseCrs.Srid == storageSrid && publicIdAttributeName is null)
        {
            return await target.FeatureReader.GetAsync(target.LayerId, target.ObjectId, cancellationToken).ConfigureAwait(false);
        }

        var query = BuildSingleResponseQuery(target.ObjectId, storageSrid, responseCrs) with
        {
            PublicIdAttributeName = publicIdAttributeName
        };
        var result = await target.FeatureReader.QueryAsync(target.LayerId, query, cancellationToken).ConfigureAwait(false);
        return result.Items.IsDefaultOrEmpty ? null : result.Items[0];
    }

    private static FeatureQuery BuildSingleResponseQuery(
        long objectId,
        int storageSrid,
        CrsDefinition responseCrs)
        => responseCrs.Srid == storageSrid
            ? new FeatureQuery
            {
                ObjectIds = ImmutableArray.Create(objectId),
                Limit = 1
            }
            : new FeatureQuery
            {
                ObjectIds = ImmutableArray.Create(objectId),
                Limit = 1,
                SpatialReferenceSrid = storageSrid,
                OutputSrid = responseCrs.Srid,
                OutputAxisOrder = responseCrs.AxisOrder
            };

    internal static string? ResolveHiddenPrimaryIdAttributeName(MetadataV2Object resource)
        => resource.FindPrimaryIdField() is { Hidden: true } identifier
            ? identifier.Name
            : null;

    public static Task<ConsistentProjectedFeatureRead> LoadProjectedConsistentWithStorageAsync(
        OgcFeatureReadTarget target,
        CrsDefinition responseCrs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target.FeatureReader);
        ArgumentNullException.ThrowIfNull(target.Resource);
        var storageSrid = target.Resource.ReadSrid() ?? 4326;
        var storageCrs = new CrsDefinition(string.Empty, storageSrid, AxisOrder.EastNorth, false);
        return ReadUntilStorageStableAsync(
            token => LoadFeatureForResponseCoreAsync(target, storageCrs, token),
            token => LoadFeatureForResponseCoreAsync(target, responseCrs, token),
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

internal enum ConsistentFeatureReadStatus
{
    Found,
    NotFound,
    Unstable,
}

internal readonly record struct OgcFeatureReadTarget
{
    public required IFeatureReader FeatureReader { get; init; }

    public required int LayerId { get; init; }

    public required MetadataV2Object Resource { get; init; }

    public required long ObjectId { get; init; }
}

internal readonly record struct ConsistentProjectedFeatureRead(
    ConsistentFeatureReadStatus Status,
    Feature? Response,
    Feature? Storage);
