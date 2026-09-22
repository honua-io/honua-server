// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Authorization.Abstractions;
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
    IFilterExpressionService? filters,
    IFieldMaskSource? fieldMasks = null) : IRelationshipStore
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

        // GeoServices carries both canonical WHERE and its translated SQL. Use the
        // canonical form when they are equivalent so each bound provider translates it.
        // Independently supplied SQL retains its original precedence and restrictions.
        if (query.SqlFilter is not null && !string.IsNullOrWhiteSpace(query.Where) && filters is not null)
        {
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

            if (query.SqlFilter.Sql == translated.SqlFilter.Sql &&
                query.SqlFilter.Parameters.SequenceEqual(translated.SqlFilter.Parameters))
            {
                query = query with { SqlFilter = null };
            }
        }

        ImmutableArray<string>? originMasks = fieldMasks is null ? null :
            await fieldMasks.ResolveAsync(origin.Resource, cancellationToken).ConfigureAwait(false);
        ImmutableArray<string>? destinationMasks = fieldMasks is null ? null :
            await fieldMasks.ResolveAsync(destination.Resource, cancellationToken).ConfigureAwait(false);
        var originReader = await ResolveReaderAsync(snapshot, origin, layerId, cancellationToken).ConfigureAwait(false);
        var destinationReader = await ResolveReaderAsync(snapshot, destination, query.RelatedLayerId!.Value, cancellationToken).ConfigureAwait(false);
        return await QueryReadersAsync(originReader, destinationReader, layerId, query, cancellationToken,
            originMasks, destinationMasks).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        ImmutableArray<string>? originMasks = null,
        ImmutableArray<string>? destinationMasks = null)
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

        // Validate caller predicates against the full mask set before allowing a join
        // key through the internal projection. Only relationship matching gets access.
        FeatureQuerySecurity.Validate(new FeatureQuery
        {
            Where = query.Where,
            SqlFilter = query.SqlFilter,
            EnforcedMaskedFields = destinationMasks
        });

        // The origin reader enforces the origin layer's row visibility, so a hidden origin row
        // never contributes a join key.
        var origins = await originReader.QueryAsync(layerId, new FeatureQuery
        {
            ObjectIds = query.ObjectIds.ToImmutableArray(),
            OutFields = [query.OriginForeignKeyField],
            EnforcedMaskedFields = InternalJoinMasks(originMasks, query.OriginForeignKeyField)
        }, cancellationToken).ConfigureAwait(false);
        var idsByKey = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var literalsByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var origin in origins.Items)
        {
            if (!TryKey(origin, query.OriginForeignKeyField, out var key, out var value))
            {
                continue;
            }

            if (!idsByKey.TryGetValue(key, out var ids))
            {
                ids = [];
                idsByKey[key] = ids;
                literalsByKey[key] = FormatJoinLiteral(value!, key);
            }

            ids.Add(origin.Id);
        }

        if (idsByKey.Count == 0)
        {
            return QueryResult<Feature>.Empty();
        }

        var joinWhere = $"\"{query.DestinationForeignKeyField}\" IN ({string.Join(",", literalsByKey.Values)})";
        SqlFragment? sqlFilter = null;
        if (query.SqlFilter is not null)
        {
            var parameters = idsByKey.Keys.Cast<object?>().ToArray();
            var placeholders = Enumerable.Range(0, parameters.Length).Select(index => "@p" + index.ToString(CultureInfo.InvariantCulture));
            var join = new SqlFragment($"attributes->>'{query.DestinationForeignKeyField}' IN ({string.Join(",", placeholders)})", parameters);
            sqlFilter = SqlFragmentHelpers.CombineSqlFilters(query.SqlFilter, join);
        }
        var outFields = query.OutFields;
        var addJoinField = outFields is { IsDefaultOrEmpty: false } projection &&
            !projection.Contains(query.DestinationForeignKeyField, StringComparer.OrdinalIgnoreCase);
        var removeJoinField = addJoinField || destinationMasks is { IsDefaultOrEmpty: false } masks &&
            masks.Contains(query.DestinationForeignKeyField, StringComparer.OrdinalIgnoreCase);
        if (addJoinField)
        {
            outFields = outFields!.Value.Add(query.DestinationForeignKeyField);
        }

        var children = await destinationReader.QueryAsync(relatedLayerId, new FeatureQuery
        {
            Where = string.IsNullOrWhiteSpace(query.Where) ? joinWhere : $"({query.Where}) AND ({joinWhere})",
            SqlFilter = sqlFilter,
            EnforcedMaskedFields = InternalJoinMasks(destinationMasks, query.DestinationForeignKeyField),
            OutFields = outFields,
            Limit = query.Limit,
            Offset = query.Offset
        }, cancellationToken).ConfigureAwait(false);
        var matched = ImmutableArray.CreateBuilder<Feature>(children.Items.Length);
        foreach (var child in children.Items)
        {
            if (!TryKey(child, query.DestinationForeignKeyField, out var key, out _) || !idsByKey.TryGetValue(key, out var ids))
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

    private static ImmutableArray<string>? InternalJoinMasks(ImmutableArray<string>? masks, string key)
        => masks is { } resolved && !resolved.IsDefault
            ? resolved.Where(field => !field.Equals(key, StringComparison.OrdinalIgnoreCase)).ToImmutableArray()
            : masks;

    private static string FormatJoinLiteral(object value, string key)
        => value switch
        {
            bool boolean => boolean ? "TRUE" : "FALSE",
            byte or sbyte or short or ushort or int or uint or long or ulong or decimal => key,
            float number when float.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
            double number when double.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
            _ => "'" + key.Replace("'", "''", StringComparison.Ordinal) + "'"
        };

    private static bool TryKey(Feature feature, string field, out string key, out object? value)
    {
        value = feature.Attributes.FirstOrDefault(attribute => attribute.Key.Equals(field, StringComparison.OrdinalIgnoreCase)).Value;
        key = value is bool boolean
            ? (boolean ? "true" : "false")
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return value is not null;
    }

    private sealed record BoundResource(MetadataV2Resource Resource, MetadataV2StorageBinding Binding, FeatureStorageMapping Mapping);
}
