// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

internal interface IRelatedRecordsService
{
    RelatedQuery BuildRelatedQuery(
        QueryRelatedRecordsParameters queryParams,
        long[] objectIds,
        MetadataV2Relationship relationship,
        int relatedStorageLayerId,
        SqlFragment? sqlFilter);

    Task<QueryResult<Feature>> ExecuteRelatedQueryAsync(
        int layerId,
        RelatedQuery query,
        CancellationToken cancellationToken);

    GroupedRelatedRecords GroupRelatedRecords(
        QueryResult<Feature> result,
        long[] objectIds,
        MetadataV2Relationship relationship,
        string objectIdFieldName,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        int? geometryPrecision,
        double? maxAllowableOffset,
        ImmutableArray<string>? outFields,
        MetadataV2Resource relatedResource,
        ImmutableArray<OrderByClause>? orderBy = null,
        bool returnCountOnly = false);
}

internal readonly record struct GroupedRelatedRecords(
    RelatedRecordGroup[] Groups,
    GeoServicesFieldInfo[] Fields,
    string ObjectIdFieldName,
    string? GeometryType,
    GeoServicesSpatialReference? SpatialReference,
    bool HasZ,
    bool HasM);

internal sealed class RelatedRecordsService : IRelatedRecordsService
{
    private readonly IRelationshipStore _relationshipStore;
    private readonly GeometryLimits _geometryLimits;
    private readonly int _maxRecordCount;

    public RelatedRecordsService(IRelationshipStore relationshipStore, IOptions<LimitsOptions> limitsOptions)
    {
        _relationshipStore = relationshipStore ?? throw new ArgumentNullException(nameof(relationshipStore));
        _geometryLimits = limitsOptions?.Value?.Geometry ?? new GeometryLimits();
        var maxRecordCount = limitsOptions?.Value?.Query?.MaxRecordCount ?? 0;
        _maxRecordCount = maxRecordCount > 0 ? maxRecordCount : new QueryLimits().MaxRecordCount;
    }

    public RelatedQuery BuildRelatedQuery(
        QueryRelatedRecordsParameters queryParams,
        long[] objectIds,
        MetadataV2Relationship relationship,
        int relatedStorageLayerId,
        SqlFragment? sqlFilter)
    {
        // returnCountOnly reports the total number of matching related records per
        // source object, so pagination (resultRecordCount/resultOffset) must not be
        // applied — otherwise the count would reflect a single page, not the total.
        var query = RelatedQuery.ForObjects(
            objectIds,
            relatedStorageLayerId,
            relationship.OriginField,
            relationship.DestinationField) with
        {
            Where = queryParams.Where,
            SqlFilter = sqlFilter,
            // One global window across every origin. An omitted resultRecordCount uses
            // maxRecordCount; the stores probe one extra row and set HasMoreResults.
            Limit = queryParams.ReturnCountOnly ? null : queryParams.ResultRecordCount ?? _maxRecordCount,
            Offset = queryParams.ReturnCountOnly ? null : queryParams.ResultOffset
        };

        // Parse outFields if specified
        if (!string.IsNullOrEmpty(queryParams.OutFields))
        {
            if (queryParams.OutFields == "*")
            {
                // Return all fields - let the query run without field filtering
                query = query with { OutFields = null };
            }
            else
            {
                var fields = queryParams.OutFields.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(f => f.Trim())
                    .ToImmutableArray();
                query = query with { OutFields = fields };
            }
        }

        return query;
    }

    public async Task<QueryResult<Feature>> ExecuteRelatedQueryAsync(
        int layerId,
        RelatedQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _relationshipStore.QueryRelatedAsync(layerId, query, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("Invalid related query.", ex);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Invalid related query format.", ex);
        }
    }

    public GroupedRelatedRecords GroupRelatedRecords(
        QueryResult<Feature> result,
        long[] objectIds,
        MetadataV2Relationship relationship,
        string objectIdFieldName,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        int? geometryPrecision,
        double? maxAllowableOffset,
        ImmutableArray<string>? outFields,
        MetadataV2Resource relatedResource,
        ImmutableArray<OrderByClause>? orderBy = null,
        bool returnCountOnly = false)
    {
        ArgumentNullException.ThrowIfNull(relatedResource);

        HashSet<string>? outFieldSet = null;
        if (outFields.HasValue && outFields.Value.Length > 0)
        {
            outFieldSet = new HashSet<string>(outFields.Value, StringComparer.OrdinalIgnoreCase);
        }

        // Esri spec: the field definitions for the returned attributes are carried
        // once at the response top level. Build them from the related layer schema
        // using the same field projection the main query response uses (#1431).
        var relatedFields = QueryFormatter.BuildQueryFields(
            relatedResource,
            outFields.HasValue && outFields.Value.Length > 0 ? outFields.Value.ToArray() : null,
            objectIdFieldName);

        // Match the main query path's attribute visibility: declared-but-Hidden fields
        // must not be served (the top-level fields schema above already drops them), while
        // undeclared runtime attributes remain visible. Without this, a Hidden (potentially
        // sensitive) column leaked through queryRelatedRecords per-record attributes.
        var allDeclaredAttributeFields = relatedResource.SchemaFields
            .Where(static field => field.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography))
            .Select(static field => field.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visibleDeclaredAttributeFields = relatedResource.SchemaFields
            .Where(static field => !field.Hidden &&
                                   field.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography))
            .Select(static field => field.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Match the main query path's temporal representations before serialization
        // loses the field-type context.
        var temporalFieldTypes = GeoServicesFieldConventions.ResolveTemporalFieldTypes(relatedResource);

        // Geometry metadata (geometryType / spatialReference / hasZ / hasM) is emitted
        // once at the response top level per the Esri queryRelatedRecords contract, and
        // only when geometry is actually returned (layers, returnGeometry=true).
        var canonicalGeometryType = relatedResource.ReadGeometryType();
        var resourceHasGeometry = canonicalGeometryType != MetadataV2GeometryType.None
            || relatedResource.FindPrimaryGeometryField() is not null;
        var emitGeometryMetadata = returnGeometry && resourceHasGeometry;

        var srid = outputSrid.HasValue && outputSrid.Value > 0
            ? outputSrid.Value
            : relatedResource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
        var spatialReference = emitGeometryMetadata
            ? new GeoServicesSpatialReference { Wkid = srid, LatestWkid = srid }
            : null;

        var effectiveGeometryLimits = GeometryOutputProcessor.CreateEffectiveLimits(
            _geometryLimits,
            geometryPrecision,
            maxAllowableOffset,
            forceSimplify: maxAllowableOffset is > 0);

        var featuresByOriginId = new Dictionary<long, List<Feature>>();

        foreach (var feature in result.Items)
        {
            if (feature.Attributes is null)
            {
                continue;
            }

            // Prefer the origin object id(s) the storage layer stamped onto each related
            // row. The relationship's foreign key (OriginField → DestinationField) is not
            // necessarily the object-id field, so grouping by the destination key value
            // alone only resolves records when the foreign key happens to equal the
            // object id — which fails for origin object ids whose foreign-key value
            // differs from their object id (e.g. large/seeded ids). The stamp carries the
            // true origin object id(s) so the relate resolves for any object id.
            if (TryReadStampedOriginObjectIds(feature, out var stampedOriginIds))
            {
                foreach (var originId in stampedOriginIds)
                {
                    AddToBucket(featuresByOriginId, originId, feature);
                }

                continue;
            }

            // Fallback: derive the origin from the destination foreign-key value. This
            // preserves behavior for stores/paths that do not stamp the origin id and for
            // relationships where the foreign key is the object-id field.
            if (feature.Attributes.TryGetValue(relationship.DestinationField, out object? fkValue) &&
                FeatureServerValueParser.TryConvertToLong(fkValue, out var fallbackOriginId))
            {
                AddToBucket(featuresByOriginId, fallbackOriginId, feature);
            }
        }

        // Apply the requested in-memory ordering to each origin object's related
        // records (Esri orderByFields). The storage query returns rows ordered by
        // objectid; orderByFields re-sorts each bucket so the emitted records honor
        // the caller's requested ordering.
        if (orderBy is { Length: > 0 } orderByClauses)
        {
            foreach (var bucket in featuresByOriginId.Values)
            {
                bucket.Sort((left, right) => CompareByOrderBy(left, right, orderByClauses));
            }
        }

        // Create a related record group for each requested object ID. Per the Esri
        // queryRelatedRecords contract, relatedRecords is a FLAT array of records
        // (each {attributes, geometry}); the JS SDK reads relatedRecords.length.
        // With returnCountOnly the group carries only the related-record count.
        var groups = objectIds.Select(objectId =>
        {
            bool hasRelatedFeatures = featuresByOriginId.TryGetValue(objectId, out List<Feature>? relatedFeatures);
            var relatedCount = hasRelatedFeatures ? relatedFeatures!.Count : 0;

            if (returnCountOnly)
            {
                return new RelatedRecordGroup
                {
                    ObjectId = objectId,
                    Count = relatedCount
                };
            }

            return new RelatedRecordGroup
            {
                ObjectId = objectId,
                // Always emit relatedRecords as an array (empty when the source object
                // has no related rows). The @arcgis/core JS SDK reads
                // group.relatedRecords.length unconditionally and throws
                // "Cannot read properties of undefined (reading 'length')" when the key
                // is absent, so an empty group must still carry relatedRecords: [].
                RelatedRecords = hasRelatedFeatures && relatedCount > 0
                    ?
                    [
                        ..relatedFeatures!.Select(f => ConvertToGeoServicesFeature(
                            f,
                            objectIdFieldName,
                            returnGeometry,
                            outputSrid,
                            returnZ,
                            returnM,
                            outFieldSet,
                            allDeclaredAttributeFields,
                            visibleDeclaredAttributeFields,
                            temporalFieldTypes,
                            effectiveGeometryLimits))
                    ]
                    : []
            };
        }).ToArray();

        // Count-only responses carry no field/geometry schema or per-record payload.
        if (returnCountOnly)
        {
            return new GroupedRelatedRecords(
                groups,
                Fields: [],
                objectIdFieldName,
                GeometryType: null,
                SpatialReference: null,
                HasZ: false,
                HasM: false);
        }

        var hasZ = false;
        var hasM = false;
        if (emitGeometryMetadata)
        {
            foreach (var group in groups)
            {
                if (group.RelatedRecords is null)
                {
                    continue;
                }

                foreach (var record in group.RelatedRecords)
                {
                    if (record.Geometry is null)
                    {
                        continue;
                    }

                    hasZ |= record.Geometry.HasZ;
                    hasM |= record.Geometry.HasM;
                }
            }
        }

        return new GroupedRelatedRecords(
            groups,
            relatedFields,
            objectIdFieldName,
            emitGeometryMetadata ? QueryFormatter.MapGeometryType(canonicalGeometryType) : null,
            spatialReference,
            hasZ,
            hasM);
    }

    private static bool IsVisibleRelatedAttribute(
        string fieldName,
        IReadOnlySet<string> allDeclaredAttributeFields,
        IReadOnlySet<string> visibleDeclaredAttributeFields)
        => visibleDeclaredAttributeFields.Contains(fieldName)
           || !allDeclaredAttributeFields.Contains(fieldName);

    private static void AddToBucket(Dictionary<long, List<Feature>> featuresByOriginId, long originId, Feature feature)
    {
        if (!featuresByOriginId.TryGetValue(originId, out var bucket))
        {
            bucket = [];
            featuresByOriginId[originId] = bucket;
        }

        bucket.Add(feature);
    }

    private static bool TryReadStampedOriginObjectIds(Feature feature, out IReadOnlyList<long> originObjectIds)
    {
        if (feature.Attributes.TryGetValue(RelatedQuery.OriginObjectIdsAttribute, out var stamped) &&
            stamped is not null)
        {
            switch (stamped)
            {
                case long[] longArray when longArray.Length > 0:
                    originObjectIds = longArray;
                    return true;
                case IEnumerable<long> enumerable:
                    var materialized = enumerable.ToList();
                    if (materialized.Count > 0)
                    {
                        originObjectIds = materialized;
                        return true;
                    }

                    break;
            }
        }

        originObjectIds = [];
        return false;
    }

    private static GeoServicesFeature ConvertToGeoServicesFeature(
        Feature feature,
        string objectIdFieldName,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        HashSet<string>? outFields,
        IReadOnlySet<string> allDeclaredAttributeFields,
        IReadOnlySet<string> visibleDeclaredAttributeFields,
        IReadOnlyDictionary<string, MetadataV2FieldType> temporalFieldTypes,
        GeometryLimits geometryLimits)
    {
        var attributes = feature.Attributes
            .Where(kvp => (outFields == null || outFields.Contains(kvp.Key)) &&
                          !FeatureAttributeVisibility.IsInternalAttribute(kvp.Key) &&
                          IsVisibleRelatedAttribute(kvp.Key, allDeclaredAttributeFields, visibleDeclaredAttributeFields))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        // Esri's related-record contract always carries the destination layer's
        // object id, even when outFields names a narrower attribute projection.
        if (!attributes.ContainsKey(objectIdFieldName))
        {
            attributes[objectIdFieldName] = GeoServicesObjectIdFieldResolver.ResolveObjectIdValue(feature, objectIdFieldName);
        }

        GeoServicesFieldConventions.CoerceTemporalAttributes(attributes, temporalFieldTypes);

        return new GeoServicesFeature
        {
            Attributes = attributes,
            IncludeGeometry = returnGeometry,
            Geometry = returnGeometry
                ? GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(
                    feature.Geometry,
                    outputSrid,
                    geometryLimits,
                    returnZ,
                    returnM)
                : null
        };
    }

    private static int CompareByOrderBy(
        Feature left,
        Feature right,
        ImmutableArray<OrderByClause> orderBy)
    {
        foreach (var clause in orderBy)
        {
            left.Attributes.TryGetValue(clause.Field, out var leftValue);
            right.Attributes.TryGetValue(clause.Field, out var rightValue);

            var comparison = CompareValues(leftValue, rightValue);
            if (comparison != 0)
            {
                return clause.Ascending ? comparison : -comparison;
            }
        }

        return 0;
    }

    private static int CompareValues(object? left, object? right)
    {
        if (left is null && right is null)
        {
            return 0;
        }

        // Nulls sort last under ascending order (CompareByOrderBy negates for DESC).
        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        if (left is IComparable comparable && left.GetType() == right.GetType())
        {
            return comparable.CompareTo(right);
        }

        if (FeatureServerValueParser.TryConvertToDouble(left, out var leftNumber) &&
            FeatureServerValueParser.TryConvertToDouble(right, out var rightNumber))
        {
            return leftNumber.CompareTo(rightNumber);
        }

        return string.Compare(
            left.ToString(),
            right.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }
}
