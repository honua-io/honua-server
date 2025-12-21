// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.FeatureServer;

/// <summary>
/// Handler for FeatureServer operations that consolidates dependencies to reduce DI coupling.
/// Addresses architectural limit of 5 dependencies per endpoint / 4 per handler.
/// </summary>
/// <remarks>
/// Initializes a new FeatureServerHandler with required dependencies.
/// Note: This handler has 6 dependencies (exceeding 4-handler limit) but reduces
/// endpoint dependency count from 6 to 1, meeting the 5-endpoint limit.
/// </remarks>
internal sealed class FeatureServerHandler(
    ILayerCatalog layerCatalog,
    IFeatureStore featureStore,
    IGeometryConverter geometryConverter,
    IQueryFormatter queryFormatter,
    IOptions<LimitsOptions> limitsOptions,
    ILogger<FeatureServerHandler> logger)
{
    private readonly ILayerCatalog _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
    private readonly IFeatureStore _featureStore = featureStore ?? throw new ArgumentNullException(nameof(featureStore));
    private readonly IGeometryConverter _geometryConverter = geometryConverter ?? throw new ArgumentNullException(nameof(geometryConverter));
    private readonly IQueryFormatter _queryFormatter = queryFormatter ?? throw new ArgumentNullException(nameof(queryFormatter));
    private readonly LimitsOptions _limitsOptions = limitsOptions?.Value ?? throw new ArgumentNullException(nameof(limitsOptions));
    private readonly ILogger<FeatureServerHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Executes a feature query operation with proper validation and formatting.
    /// </summary>
    public async Task<IResult> HandleQueryFeaturesAsync(
        string serviceId,
        int layerId,
        QueryParameters queryParams,
        CancellationToken cancellationToken = default)
    {
        try
        {
            FeatureServerLog.QueryRequested(_logger, serviceId, layerId, queryParams.Where);

            // Validate service and layer existence
            ServiceDefinition? service = await _layerCatalog.GetServiceAsync(serviceId, cancellationToken);
            if (service == null)
            {
                FeatureServerLog.ServiceNotFound(_logger, serviceId);
                return EsriErrorHelpers.CreateNotFoundError($"Service '{serviceId}' not found");
            }

            LayerDefinition? layer = service.Layers.FirstOrDefault(l => l.Id == layerId);
            if (layer == null)
            {
                FeatureServerLog.LayerNotFound(_logger, serviceId, layerId);
                return EsriErrorHelpers.CreateNotFoundError($"Layer {layerId} not found in service '{serviceId}'");
            }

            if (queryParams.ResultRecordCount is < 1)
            {
                return EsriErrorHelpers.CreateBadRequestError(
                    "Invalid query parameters",
                    [$"{nameof(QueryParameters.ResultRecordCount)} must be greater than 0"]);
            }

            if (queryParams.ResultOffset is < 0)
            {
                return EsriErrorHelpers.CreateBadRequestError(
                    "Invalid query parameters",
                    [$"{nameof(QueryParameters.ResultOffset)} must be 0 or greater"]);
            }

            // Apply limits enforcement
            QueryParameters? validatedParams = ApplyQueryLimits(queryParams, _limitsOptions.Query);
            if (validatedParams == null)
            {
                return EsriErrorHelpers.CreateBadRequestError(
                    "Query parameters exceed configured limits",
                    [$"Maximum record count: {_limitsOptions.Query.MaxRecordCount}, Maximum offset: {_limitsOptions.Query.MaxOffset}"]);
            }

            // Build query from validated parameters
            FeatureQuery query = BuildFeatureQuery(validatedParams, service);

            // Execute query
            QueryResult<Feature> result = await ExecuteQueryWithValidation(layerId, query, cancellationToken);

            // Format response using QueryFormatter
            string[]? outFields = string.IsNullOrEmpty(validatedParams.OutFields) ? null :
                [.. validatedParams.OutFields.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim())];

            (object? formattedResponse, string? contentType) = _queryFormatter.FormatQueryResult(
                result,
                layer,
                validatedParams.F ?? "json",
                validatedParams.ReturnGeometry,
                outFields);

            FeatureServerLog.QueryCompleted(_logger, serviceId, layerId, result.Items.Length, result.TotalCount);

            // Return response with appropriate content type and JSON context
            return validatedParams.F?.ToLowerInvariant() switch
            {
                "geojson" => Results.Json(formattedResponse, FeatureServerJsonContext.Default.GeoJsonFeatureSet, contentType: contentType),
                _ => Results.Json(formattedResponse, FeatureServerJsonContext.Default.QueryResponse, contentType: contentType)
            };
        }
        catch (InvalidOperationException ex)
        {
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);

            return EsriErrorHelpers.CreateBadRequestError(
                "Invalid query parameters",
                [ex.Message]);
        }
        catch (Exception ex)
        {
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);

            return EsriErrorHelpers.CreateInternalServerError(
                "Query execution failed",
                [ex.Message]);
        }
    }

    /// <summary>
    /// Executes a query for related records with proper validation and formatting.
    /// </summary>
    /// <summary>
    /// Executes a query for related records with proper validation and formatting.
    /// </summary>
    public async Task<IResult> HandleQueryRelatedRecordsAsync(
        string serviceId,
        int layerId,
        QueryRelatedRecordsParameters queryParams,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string objectIdsString = string.Join(",", queryParams.ObjectIds);
            FeatureServerLog.RelatedRecordsQueryRequested(_logger, serviceId, layerId, objectIdsString, queryParams.RelationshipId);

            // Validate service and layer existence
            ServiceDefinition? service = await _layerCatalog.GetServiceAsync(serviceId, cancellationToken);
            if (service == null)
            {
                FeatureServerLog.ServiceNotFound(_logger, serviceId);
                return EsriErrorHelpers.CreateNotFoundError($"Service '{serviceId}' not found");
            }

            LayerDefinition? layer = service.Layers.FirstOrDefault(l => l.Id == layerId);
            if (layer == null)
            {
                FeatureServerLog.LayerNotFound(_logger, serviceId, layerId);
                return EsriErrorHelpers.CreateNotFoundError($"Layer {layerId} not found in service '{serviceId}'");
            }

            // Validate required parameters (these should already be validated by parameter parsing)
            if (queryParams.ObjectIds.Length == 0)
            {
                return EsriErrorHelpers.CreateBadRequestError(
                    "Invalid query parameters",
                    ["objectIds parameter is required"]);
            }

            // Validate relationship exists
            var relationshipMaybe = await _layerCatalog.GetRelationshipAsync(layerId, queryParams.RelationshipId, cancellationToken);
            if (relationshipMaybe == null)
            {
                FeatureServerLog.RelationshipNotFound(_logger, layerId, queryParams.RelationshipId);
                return EsriErrorHelpers.CreateNotFoundError(
                    $"Relationship {queryParams.RelationshipId} not found for layer {layerId}");
            }

            var relationship = relationshipMaybe.Value;

            // Apply limits enforcement
            QueryRelatedRecordsParameters? validatedParams = ApplyRelatedRecordsLimits(queryParams, _limitsOptions.Query);
            if (validatedParams == null)
            {
                return EsriErrorHelpers.CreateBadRequestError(
                    "Query parameters exceed configured limits",
                    [$"Maximum record count: {_limitsOptions.Query.MaxRecordCount}"]);
            }

            // Get related layer information
            var relatedLayer = service.Layers.FirstOrDefault(l => l.Id == relationship!.RelatedLayerId);
            if (relatedLayer == null)
            {
                FeatureServerLog.LayerNotFound(_logger, serviceId, relationship.RelatedLayerId);
                return EsriErrorHelpers.CreateNotFoundError(
                    $"Related layer {relationship.RelatedLayerId} not found in service '{serviceId}'");
            }

            // Convert long[] to int[] for the RelatedQuery
            int[] objectIdsAsInt = [.. queryParams.ObjectIds.Select(id => (int)id)];

            // Build related query from validated parameters
            RelatedQuery relatedQuery = BuildRelatedQuery(validatedParams, objectIdsAsInt, (Relationship)relationship);

            // Execute related query
            QueryResult<Feature> result = await ExecuteRelatedQueryWithValidation(layerId, relatedQuery, cancellationToken);

            // Group results by origin object ID
            RelatedRecordGroup[] relatedRecordGroups = GroupRelatedRecords(result, objectIdsAsInt, (Relationship)relationship, validatedParams.ReturnGeometry);

            // Build response
            var response = new QueryRelatedRecordsResponse
            {
                RelatedRecordGroups = relatedRecordGroups
            };

            FeatureServerLog.RelatedRecordsQueryCompleted(_logger, serviceId, layerId,
                relatedRecordGroups.Sum(g => g.RelatedRecords?.Features?.Length ?? 0), relatedRecordGroups.Length);

            return Results.Json(response, FeatureServerJsonContext.Default.QueryRelatedRecordsResponse, contentType: "application/json");
        }
        catch (InvalidOperationException ex)
        {
            FeatureServerLog.RelatedRecordsQueryFailed(_logger, serviceId, layerId, ex.Message, ex);

            return EsriErrorHelpers.CreateBadRequestError(
                "Invalid query parameters",
                [ex.Message]);
        }
        catch (Exception ex)
        {
            FeatureServerLog.RelatedRecordsQueryFailed(_logger, serviceId, layerId, ex.Message, ex);

            return EsriErrorHelpers.CreateInternalServerError(
                "Related records query execution failed",
                [ex.Message]);
        }
    }

    /// <summary>
    /// Applies query limits enforcement and returns validated parameters or null if limits exceeded.
    /// </summary>
    private static QueryParameters? ApplyQueryLimits(QueryParameters queryParams, QueryLimits limits)
    {
        // Validate and apply record count limits
        int recordCount = queryParams.ResultRecordCount ?? limits.DefaultRecordCount;
        if (recordCount > limits.MaxRecordCount)
        {
            return null;
        }

        // Validate offset limits
        int offset = queryParams.ResultOffset ?? 0;
        if (offset > limits.MaxOffset)
        {
            return null;
        }

        // Return validated parameters with applied defaults
        return new QueryParameters
        {
            Where = queryParams.Where,
            OutFields = queryParams.OutFields,
            ReturnGeometry = queryParams.ReturnGeometry,
            F = queryParams.F,
            ResultOffset = offset,
            ResultRecordCount = recordCount,
            Geometry = queryParams.Geometry,
            GeometryType = queryParams.GeometryType,
            SpatialRel = queryParams.SpatialRel
        };
    }

    /// <summary>
    /// Builds a FeatureQuery from query parameters
    /// </summary>
    private FeatureQuery BuildFeatureQuery(QueryParameters queryParams, ServiceDefinition service)
    {
        var query = new FeatureQuery
        {
            Where = queryParams.Where,
            Offset = queryParams.ResultOffset,
            Limit = queryParams.ResultRecordCount ?? service.MaxRecordCount
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

        // Parse spatial filter if specified
        if (!string.IsNullOrEmpty(queryParams.Geometry))
        {
            try
            {
                SpatialFilter spatialFilter = ParseSpatialFilter(queryParams.Geometry, queryParams.SpatialRel);
                query = query with { SpatialFilter = spatialFilter };
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"Invalid spatial parameters: {ex.Message}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Invalid geometry: {ex.Message}");
            }
        }

        return query;
    }

    /// <summary>
    /// Parses Esri JSON geometry and spatial relationship into a SpatialFilter
    /// </summary>
    private SpatialFilter ParseSpatialFilter(string geometry, string? spatialRel)
    {
        // Convert Esri JSON geometry to WKB bytes
        byte[] wkbBytes = ConvertEsriJsonToWkb(geometry);

        // Map Esri spatial relationship to enum
        SpatialRelationship relationship = ParseSpatialRelationship(spatialRel);

        return new SpatialFilter
        {
            Geometry = wkbBytes,
            SpatialRelationship = relationship
        };
    }

    /// <summary>
    /// Maps Esri spatial relationship strings to SpatialRelationship enum
    /// </summary>
    private static SpatialRelationship ParseSpatialRelationship(string? spatialRel)
    {
        return spatialRel?.ToLowerInvariant() switch
        {
            "esrispatialrelintersects" or null => SpatialRelationship.Intersects,
            "esrispatialrelcontains" => SpatialRelationship.Contains,
            "esrispatialrelwithin" => SpatialRelationship.Within,
            "esrispatialrelenvelopeintersects" => SpatialRelationship.EnvelopeIntersects,
            _ => throw new ArgumentException($"Unsupported spatial relationship: {spatialRel}")
        };
    }

    /// <summary>
    /// Converts Esri JSON geometry to WKB bytes using the geometry converter service
    /// </summary>
    private byte[] ConvertEsriJsonToWkb(string esriJsonGeometry) => _geometryConverter.ConvertEsriJsonToWkb(esriJsonGeometry);

    /// <summary>
    /// Executes a query with validation error handling
    /// </summary>
    private async Task<QueryResult<Feature>> ExecuteQueryWithValidation(int layerId, FeatureQuery query, CancellationToken cancellationToken)
    {
        try
        {
            return await _featureStore.QueryAsync(layerId, query, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Invalid query: {ex.Message}");
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Invalid query format: {ex.Message}");
        }
        catch (Exception ex) when (ex.Message.Contains("syntax") || ex.Message.Contains("SQL") || ex.Message.Contains("parse"))
        {
            throw new InvalidOperationException($"Invalid query syntax: {ex.Message}");
        }
    }


    /// <summary>
    /// Applies related records query limits enforcement and returns validated parameters or null if limits exceeded.
    /// </summary>
    private static QueryRelatedRecordsParameters? ApplyRelatedRecordsLimits(QueryRelatedRecordsParameters queryParams, QueryLimits limits)
    {
        // Validate and apply record count limits
        int recordCount = queryParams.ResultRecordCount ?? limits.DefaultRecordCount;
        if (recordCount > limits.MaxRecordCount)
        {
            return null;
        }

        // Return validated parameters with applied defaults
        return new QueryRelatedRecordsParameters
        {
            ObjectIds = queryParams.ObjectIds,
            RelationshipId = queryParams.RelationshipId,
            OutFields = queryParams.OutFields,
            ReturnGeometry = queryParams.ReturnGeometry,
            F = queryParams.F,
            ResultRecordCount = recordCount,
            Where = queryParams.Where
        };
    }

    /// <summary>
    /// Builds a FeatureQuery for related records from query parameters
    /// </summary>
    private static RelatedQuery BuildRelatedQuery(QueryRelatedRecordsParameters queryParams, int[] objectIds, Relationship relationship)
    {
        var query = new RelatedQuery
        {
            ObjectIds = objectIds,
            Relationship = relationship,
            Where = queryParams.Where,
            Limit = queryParams.ResultRecordCount
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
    /// Executes a related records query with validation error handling
    /// </summary>
    private async Task<QueryResult<Feature>> ExecuteRelatedQueryWithValidation(int layerId, RelatedQuery query, CancellationToken cancellationToken)
    {
        try
        {
            return await _featureStore.QueryRelatedAsync(layerId, query, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Invalid related query: {ex.Message}");
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Invalid related query format: {ex.Message}");
        }
        catch (Exception ex) when (ex.Message.Contains("syntax") || ex.Message.Contains("SQL") || ex.Message.Contains("parse"))
        {
            throw new InvalidOperationException($"Invalid related query syntax: {ex.Message}");
        }
    }

    /// <summary>
    /// Groups related records by their origin object IDs
    /// </summary>
    private static RelatedRecordGroup[] GroupRelatedRecords(QueryResult<Feature> result, int[] objectIds, Relationship relationship, bool returnGeometry = true)
    {
        // Group features by their foreign key values (which correspond to origin object IDs)
        var featuresByOriginId = result.Items
            .GroupBy(feature =>
                // Get the foreign key value from the feature
                feature.Attributes?.TryGetValue(relationship.DestinationForeignKeyField, out object? fkValue) == true ? (fkValue?.ToString()) : null)
            .Where(group => group.Key != null)
            .ToDictionary(group => int.Parse(group.Key!), group => group.ToArray());

        // Create a related record group for each requested object ID
        return [.. objectIds.Select(objectId =>
        {
            bool hasRelatedFeatures = featuresByOriginId.TryGetValue(objectId, out Feature[]? relatedFeatures);

            return new RelatedRecordGroup
            {
                ObjectId = objectId,
                RelatedRecords = hasRelatedFeatures && relatedFeatures!.Length > 0
                    ? new RelatedRecords
                    {
                        Features = [.. relatedFeatures.Select(f => ConvertToEsriFeature(f, returnGeometry))]
                    }
                    : null
            };
        })];
    }

    /// <summary>
    /// Converts a Feature to EsriFeature for API responses
    /// </summary>
    private static EsriFeature ConvertToEsriFeature(Feature feature, bool returnGeometry)
    {
        var attributes = feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return new EsriFeature
        {
            Attributes = attributes,
            Geometry = returnGeometry ? ConvertGeometryToEsriFormat(feature.Geometry) : null
        };
    }

    /// <summary>
    /// Converts WKB geometry to Esri format
    /// </summary>
    private static EsriGeometry? ConvertGeometryToEsriFormat(byte[]? wkbGeometry)
    {
        if (wkbGeometry == null || wkbGeometry.Length < 21)
            return null;

        // Detect endianness (1 = little-endian, 0 = big-endian)
        bool isLittleEndian = wkbGeometry[0] == 1;
        if (!isLittleEndian && wkbGeometry[0] != 0)
            return null; // Invalid endianness marker

        // Read geometry type with proper endianness
        uint geometryType = isLittleEndian
            ? BitConverter.ToUInt32(wkbGeometry, 1)
            : BitConverter.ToUInt32([.. wkbGeometry.AsSpan(1, 4).ToArray().Reverse()], 0);

        // Only support point geometries for now
        if (geometryType != 1)
        {
            // Return null for unsupported geometry types
            // TODO: Add support for LineString (2), Polygon (3), MultiPoint (4), etc.
            return null;
        }

        // Read coordinates with proper endianness
        double x, y;
        if (isLittleEndian)
        {
            x = BitConverter.ToDouble(wkbGeometry, 5);  // X coordinate at offset 5
            y = BitConverter.ToDouble(wkbGeometry, 13); // Y coordinate at offset 13
        }
        else
        {
            byte[] xBytes = [.. wkbGeometry.AsSpan(5, 8).ToArray().Reverse()];
            byte[] yBytes = [.. wkbGeometry.AsSpan(13, 8).ToArray().Reverse()];
            x = BitConverter.ToDouble(xBytes, 0);
            y = BitConverter.ToDouble(yBytes, 0);
        }

        // TODO: Extract actual SRID from WKB instead of defaulting to 4326
        // For now, use Web Mercator (3857) if coordinates suggest projected data, otherwise WGS84 (4326)
        int srid = (Math.Abs(x) > 180 || Math.Abs(y) > 90) ? 3857 : 4326;

        return new EsriGeometry
        {
            X = x,
            Y = y,
            SpatialReference = new EsriSpatialReference { Wkid = srid }
        };
    }

}
