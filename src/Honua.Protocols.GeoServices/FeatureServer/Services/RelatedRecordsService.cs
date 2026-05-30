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
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Services;
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
    /// <returns>Grouped related record results</returns>
    RelatedRecordGroup[] GroupRelatedRecords(
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
        ImmutableArray<string>? outFields);
}

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
    public RelatedRecordGroup[] GroupRelatedRecords(
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
        ImmutableArray<string>? outFields)
    {
        HashSet<string>? outFieldSet = null;
        if (outFields.HasValue && outFields.Value.Length > 0)
        {
            outFieldSet = new HashSet<string>(outFields.Value, StringComparer.OrdinalIgnoreCase);
        }

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

        // Create a related record group for each requested object ID
        return [.. objectIds.Select(objectId =>
        {
            bool hasRelatedFeatures = featuresByOriginId.TryGetValue(objectId, out List<Feature>? relatedFeatures);
            var spatialReference = outputSrid.HasValue && outputSrid.Value > 0
                ? new GeoServicesSpatialReference { Wkid = outputSrid.Value, LatestWkid = outputSrid.Value }
                : null;

            return new RelatedRecordGroup
            {
                ObjectId = objectId,
                RelatedRecords = hasRelatedFeatures && relatedFeatures!.Count > 0
                    ? new RelatedRecords
                    {
                        ObjectIdFieldName = objectIdFieldName,
                        SpatialReference = spatialReference,
                        Features =
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
                    }
                    : null
            };
        })];
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
