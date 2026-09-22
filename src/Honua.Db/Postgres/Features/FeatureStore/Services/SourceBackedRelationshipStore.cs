// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Routes related-record queries that involve a source-backed resource (for example an imported
/// table) through the same storage-bound readers that serve direct queries, so both sides read
/// their actual table and connection and each side's read policy is enforced by its own reader.
/// Relationships between managed resources keep using the managed relationship store unchanged.
/// </summary>
internal sealed class SourceBackedRelationshipStore(
    IRelationshipStore managedStore,
    IMetadataV2GraphProvider? metadata,
    FeatureProviderQueryRouter? router,
    IFilterExpressionService? filters) : IRelationshipStore
{
    /// <inheritdoc />
    public async Task<QueryResult<Feature>> QueryRelatedAsync(int layerId, RelatedQuery query, CancellationToken cancellationToken = default)
    {
        if (metadata is null || router is null)
        {
            return await managedStore.QueryRelatedAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        }

        // Resolve both sides from one snapshot so a concurrent catalog change cannot pair an
        // origin from one graph version with a destination from another.
        var snapshot = await metadata.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var origin = FindMapping(snapshot, layerId);
        var destination = query.RelatedLayerId is int relatedId ? FindMapping(snapshot, relatedId) : null;
        if (origin?.Mapping.IsSourceBacked != true && destination?.Mapping.IsSourceBacked != true)
        {
            return await managedStore.QueryRelatedAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        }

        if (origin is null || destination is null)
        {
            throw new InvalidOperationException("Both relationship resources must resolve to storage bindings.");
        }

        // Translate a raw caller filter before adding the join predicate: the bound reader gives a
        // SqlFilter precedence over Where, so adding the join as a SqlFilter would otherwise
        // silently discard the caller's filter.
        if (query.SqlFilter is null && !string.IsNullOrWhiteSpace(query.Where))
        {
            if (filters is null)
            {
                throw new ArgumentException("Related query filters are not supported for this relationship.");
            }

            var parsed = filters.Parse(FilterLanguage.ArcGisSql, query.Where);
            if (!parsed.IsSuccess || parsed.Expression is null)
            {
                throw new ArgumentException("Invalid related query filter.");
            }

            var translated = filters.Translate(parsed.Expression, destination.Resource);
            if (!translated.IsSuccess || translated.SqlFilter is null)
            {
                throw new ArgumentException("Unsupported related query filter.");
            }

            query = query with { SqlFilter = translated.SqlFilter };
        }

        var originReader = await ResolveReaderAsync(snapshot, origin, layerId, cancellationToken).ConfigureAwait(false);
        var destinationReader = await ResolveReaderAsync(snapshot, destination, query.RelatedLayerId!.Value, cancellationToken).ConfigureAwait(false);
        return await QueryReadersAsync(originReader, destinationReader, layerId, query, cancellationToken).ConfigureAwait(false);
    }

    private static BoundResource? FindMapping(MetadataV2GraphSnapshot snapshot, int layerId)
    {
        if (!snapshot.Index.StorageBindingsByStorageLayerId.TryGetValue(layerId, out var binding) ||
            !snapshot.Index.ResourcesById.TryGetValue(binding.ResourceId, out var resource))
        {
            return null;
        }

        return new BoundResource(resource, binding, FeatureStorageMapping.FromMetadata(resource, binding));
    }

    private Task<IFeatureReader> ResolveReaderAsync(MetadataV2GraphSnapshot snapshot, BoundResource resource, int layerId, CancellationToken cancellationToken)
    {
        // The lookup yields an empty sequence for a resource without publications.
        var publication = snapshot.Index.PublicationsByResource[resource.Resource.Metadata.Id]
            .FirstOrDefault(candidate => snapshot.ResolveStorageBinding(candidate)?.Metadata.Id == resource.Binding.Metadata.Id)
            ?? throw new InvalidOperationException("Relationship storage binding has no publication.");
        if (!snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service))
        {
            throw new InvalidOperationException("Relationship publication has no service.");
        }

        return router!.ResolveReaderAsync(snapshot, service, resource.Resource, publication, layerId, FeatureProviderReadOperation.Query, cancellationToken);
    }

    /// <summary>
    /// Reads the origin rows' join keys, then the destination rows carrying those keys, and stamps
    /// each destination row with every origin object id that shares its key.
    /// </summary>
    internal static async Task<QueryResult<Feature>> QueryReadersAsync(
        IFeatureReader originReader,
        IFeatureReader destinationReader,
        int layerId,
        RelatedQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query.OriginForeignKeyField);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.DestinationForeignKeyField);
        if (query.RelatedLayerId is not int relatedLayerId ||
            !FeatureQueryBuilder.IsValidFieldName(query.OriginForeignKeyField) ||
            !FeatureQueryBuilder.IsValidFieldName(query.DestinationForeignKeyField))
        {
            throw new ArgumentException("Invalid relationship fields or related layer id.");
        }

        if (query.ObjectIds.Length == 0)
        {
            return QueryResult<Feature>.Empty();
        }

        // The origin reader enforces the origin layer's row visibility, so a hidden origin row
        // never contributes a join key.
        var origins = await originReader.QueryAsync(layerId, new FeatureQuery
        {
            ObjectIds = query.ObjectIds.ToImmutableArray(),
            OutFields = [query.OriginForeignKeyField]
        }, cancellationToken).ConfigureAwait(false);
        var idsByKey = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var origin in origins.Items)
        {
            if (!TryKey(origin, query.OriginForeignKeyField, out var key))
            {
                continue;
            }

            if (!idsByKey.TryGetValue(key, out var ids))
            {
                ids = [];
                idsByKey[key] = ids;
            }

            ids.Add(origin.Id);
        }

        if (idsByKey.Count == 0)
        {
            return QueryResult<Feature>.Empty();
        }

        var parameters = idsByKey.Keys.Cast<object?>().ToArray();
        var placeholders = Enumerable.Range(0, parameters.Length).Select(index => "@p" + index.ToString(CultureInfo.InvariantCulture));
        var join = new SqlFragment($"attributes->>'{query.DestinationForeignKeyField}' IN ({string.Join(",", placeholders)})", parameters);
        var outFields = query.OutFields;
        var removeJoinField = outFields is { IsDefaultOrEmpty: false } projection &&
            !projection.Contains(query.DestinationForeignKeyField, StringComparer.OrdinalIgnoreCase);
        if (removeJoinField)
        {
            outFields = outFields!.Value.Add(query.DestinationForeignKeyField);
        }

        var children = await destinationReader.QueryAsync(relatedLayerId, new FeatureQuery
        {
            SqlFilter = SqlFragmentHelpers.CombineSqlFilters(query.SqlFilter, join),
            OutFields = outFields,
            Limit = query.Limit,
            Offset = query.Offset
        }, cancellationToken).ConfigureAwait(false);
        var matched = ImmutableArray.CreateBuilder<Feature>(children.Items.Length);
        foreach (var child in children.Items)
        {
            if (!TryKey(child, query.DestinationForeignKeyField, out var key) || !idsByKey.TryGetValue(key, out var ids))
            {
                continue;
            }

            var attributes = child.Attributes;
            if (removeJoinField)
            {
                attributes = attributes.RemoveRange(attributes.Keys.Where(name => name.Equals(query.DestinationForeignKeyField, StringComparison.OrdinalIgnoreCase)));
            }

            matched.Add(child with { Attributes = attributes.SetItem(RelatedQuery.OriginObjectIdsAttribute, ids.ToArray()) });
        }

        return children with { Items = matched.ToImmutable() };
    }

    private static bool TryKey(Feature feature, string field, out string key)
    {
        var value = feature.Attributes.FirstOrDefault(attribute => attribute.Key.Equals(field, StringComparison.OrdinalIgnoreCase)).Value;
        key = value is bool boolean
            ? (boolean ? "true" : "false")
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return value is not null;
    }

    private sealed record BoundResource(MetadataV2Resource Resource, MetadataV2StorageBinding Binding, FeatureStorageMapping Mapping);
}
