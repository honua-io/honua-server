// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
using Npgsql;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Service responsible for processing related records queries for FeatureServer operations.
/// Handles query building, execution, and result grouping for relationship-based queries.
/// </summary>
internal interface IRelatedRecordsService
{
    /// <summary>
    /// Builds a RelatedQuery from query parameters.
    /// </summary>
    /// <param name="queryParams">Query parameters for related records</param>
    /// <param name="objectIds">Object IDs to find related records for</param>
    /// <param name="relationship">Relationship definition</param>
    /// <param name="relatedStorageLayerId">Storage layer id of the related resource</param>
    /// <param name="sqlFilter">Optional SQL filter fragment to apply</param>
    /// <returns>Configured RelatedQuery</returns>
    RelatedQuery BuildRelatedQuery(
        QueryRelatedRecordsParameters queryParams,
        long[] objectIds,
        MetadataV2Relationship relationship,
        int relatedStorageLayerId,
        SqlFragment? sqlFilter);

    /// <summary>
    /// Executes a related records query with validation error handling.
    /// </summary>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="query">Related query to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Query result with related features</returns>
    Task<QueryResult<Feature>> ExecuteRelatedQueryAsync(
        int layerId,
        RelatedQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Groups related records by their origin object IDs for API response.
    /// </summary>
    /// <param name="result">Query result containing related features</param>
    /// <param name="objectIds">Original object IDs</param>
    /// <param name="relationship">Relationship definition</param>
    /// <param name="objectIdFieldName">Field name used for object identifiers</param>
    /// <param name="returnGeometry">Whether to include geometry in response</param>
    /// <param name="outputSrid">Output spatial reference identifier</param>
    /// <param name="returnZ">Whether to include Z values in output geometry</param>
    /// <param name="returnM">Whether to include M values in output geometry</param>
    /// <param name="geometryPrecision">Output geometry precision override</param>
    /// <param name="maxAllowableOffset">Output geometry simplification tolerance override</param>
    /// <param name="outFields">Fields to include in response</param>
    /// <param name="relatedResource">Canonical metadata for the related layer used to populate field schema</param>
    /// <returns>
    /// Grouped related record results plus the shared field/geometry metadata that
    /// the Esri queryRelatedRecords contract emits at the response top level.
    /// </returns>
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
        MetadataV2Resource relatedResource);
}

/// <summary>
/// Grouped related records together with the shared metadata (field schema,
/// object-id field name, spatial reference) that the Esri queryRelatedRecords
/// response carries once at the top level rather than per group.
/// </summary>
/// <param name="Groups">Related record groups, one per requested object id.</param>
/// <param name="Fields">Field definitions for the returned attributes.</param>
/// <param name="ObjectIdFieldName">Object id field name for the related records.</param>
/// <param name="GeometryType">Esri geometry-type token for related records that include geometry; null for tables or geometry-suppressed responses.</param>
/// <param name="SpatialReference">Spatial reference for returned geometries; null when no geometry is emitted.</param>
/// <param name="HasZ">Whether any returned geometry carries Z values.</param>
/// <param name="HasM">Whether any returned geometry carries M values.</param>
internal readonly record struct GroupedRelatedRecords(
    RelatedRecordGroup[] Groups,
    GeoServicesFieldInfo[] Fields,
    string ObjectIdFieldName,
    string? GeometryType,
    GeoServicesSpatialReference? SpatialReference,
    bool HasZ,
    bool HasM);

/// <summary>
/// Implementation of related records processing for FeatureServer operations.
/// </summary>
internal sealed class RelatedRecordsService : IRelatedRecordsService
{
    private readonly IRelationshipStore _relationshipStore;
    private readonly GeometryLimits _geometryLimits;

    public RelatedRecordsService(IRelationshipStore relationshipStore, IOptions<LimitsOptions> limitsOptions)
    {
        _relationshipStore = relationshipStore ?? throw new ArgumentNullException(nameof(relationshipStore));
        _geometryLimits = limitsOptions?.Value?.Geometry ?? new GeometryLimits();
    }

    /// <summary>
    /// Builds a RelatedQuery from query parameters.
    /// </summary>
    public RelatedQuery BuildRelatedQuery(
        QueryRelatedRecordsParameters queryParams,
        long[] objectIds,
        MetadataV2Relationship relationship,
        int relatedStorageLayerId,
        SqlFragment? sqlFilter)
    {
        var query = RelatedQuery.ForObjects(
            objectIds,
            relatedStorageLayerId,
            relationship.OriginField,
            relationship.DestinationField) with
        {
            Where = queryParams.Where,
            SqlFilter = sqlFilter,
            Limit = queryParams.ResultRecordCount,
            Offset = queryParams.ResultOffset
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

    /// <summary>
    /// Executes a related records query with validation error handling.
    /// </summary>
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
        catch (PostgresException ex) when (QueryExceptionClassifier.IsInvalidQuerySyntax(ex))
        {
            throw new InvalidOperationException("Invalid related query syntax.", ex);
        }
    }

    /// <summary>
    /// Groups related records by their origin object IDs for API response.
    /// </summary>
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
        MetadataV2Resource relatedResource)
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
            if (feature.Attributes?.TryGetValue(relationship.DestinationField, out object? fkValue) == true &&
                FeatureServerValueParser.TryConvertToLong(fkValue, out var originId))
            {
                if (!featuresByOriginId.TryGetValue(originId, out var bucket))
                {
                    bucket = [];
                    featuresByOriginId[originId] = bucket;
                }

                bucket.Add(feature);
            }
        }

        // Create a related record group for each requested object ID. Per the Esri
        // queryRelatedRecords contract, relatedRecords is a FLAT array of records
        // (each {attributes, geometry}); the JS SDK reads relatedRecords.length.
        var groups = objectIds.Select(objectId =>
        {
            bool hasRelatedFeatures = featuresByOriginId.TryGetValue(objectId, out List<Feature>? relatedFeatures);

            return new RelatedRecordGroup
            {
                ObjectId = objectId,
                RelatedRecords = hasRelatedFeatures && relatedFeatures!.Count > 0
                    ?
                    [
                        ..relatedFeatures!.Select(f => ConvertToGeoServicesFeature(
                            f,
                            returnGeometry,
                            outputSrid,
                            returnZ,
                            returnM,
                            outFieldSet,
                            effectiveGeometryLimits))
                    ]
                    : null
            };
        }).ToArray();

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

    /// <summary>
    /// Converts a Feature to GeoServicesFeature for API responses.
    /// </summary>
    private static GeoServicesFeature ConvertToGeoServicesFeature(
        Feature feature,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        HashSet<string>? outFields,
        GeometryLimits geometryLimits)
    {
        var attributes = outFields == null
            ? feature.Attributes
                .Where(kvp => !FeatureAttributeVisibility.IsInternalAttribute(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            : feature.Attributes
                .Where(kvp => outFields.Contains(kvp.Key) &&
                              !FeatureAttributeVisibility.IsInternalAttribute(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

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
}
