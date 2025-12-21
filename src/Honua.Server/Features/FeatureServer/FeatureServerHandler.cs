// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
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
internal sealed class FeatureServerHandler
{
    private readonly ILayerCatalog _layerCatalog;
    private readonly IFeatureStore _featureStore;
    private readonly IGeometryConverter _geometryConverter;
    private readonly IQueryFormatter _queryFormatter;
    private readonly LimitsOptions _limitsOptions;
    private readonly ILogger<FeatureServerHandler> _logger;

    /// <summary>
    /// Initializes a new FeatureServerHandler with required dependencies.
    /// Note: This handler has 6 dependencies (exceeding 4-handler limit) but reduces
    /// endpoint dependency count from 6 to 1, meeting the 5-endpoint limit.
    /// </summary>
    public FeatureServerHandler(
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        IGeometryConverter geometryConverter,
        IQueryFormatter queryFormatter,
        IOptions<LimitsOptions> limitsOptions,
        ILogger<FeatureServerHandler> logger)
    {
        _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        _featureStore = featureStore ?? throw new ArgumentNullException(nameof(featureStore));
        _geometryConverter = geometryConverter ?? throw new ArgumentNullException(nameof(geometryConverter));
        _queryFormatter = queryFormatter ?? throw new ArgumentNullException(nameof(queryFormatter));
        _limitsOptions = limitsOptions?.Value ?? throw new ArgumentNullException(nameof(limitsOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
            var service = await _layerCatalog.GetServiceAsync(serviceId, cancellationToken);
            if (service == null)
            {
                FeatureServerLog.ServiceNotFound(_logger, serviceId);
                return EsriErrorHelpers.CreateNotFoundError($"Service '{serviceId}' not found");
            }

            var layer = service.Layers.FirstOrDefault(l => l.Id == layerId);
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
            var validatedParams = ApplyQueryLimits(queryParams, _limitsOptions.Query);
            if (validatedParams == null)
            {
                return EsriErrorHelpers.CreateBadRequestError(
                    "Query parameters exceed configured limits",
                    [$"Maximum record count: {_limitsOptions.Query.MaxRecordCount}, Maximum offset: {_limitsOptions.Query.MaxOffset}"]);
            }

            // Build query from validated parameters
            var query = BuildFeatureQuery(validatedParams, service);

            // Execute query
            var result = await ExecuteQueryWithValidation(layerId, query, cancellationToken);

            // Format response using QueryFormatter
            var outFields = string.IsNullOrEmpty(validatedParams.OutFields) ? null :
                validatedParams.OutFields.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(f => f.Trim())
                    .ToArray();

            var (formattedResponse, contentType) = _queryFormatter.FormatQueryResult(
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
    /// Handles applyEdits requests for adding, updating, and deleting features
    /// </summary>
    public async Task<IResult> HandleApplyEditsAsync(
        string serviceId,
        int layerId,
        ApplyEditsRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            FeatureServerLog.ApplyEditsRequested(_logger, serviceId, layerId,
                request.Adds?.Length ?? 0,
                request.Updates?.Length ?? 0,
                request.Deletes?.Length ?? 0);

            // Validate service and layer exist
            var service = await _layerCatalog.GetServiceAsync(serviceId, cancellationToken);
            if (service == null)
            {
                FeatureServerLog.ServiceNotFound(_logger, serviceId);
                return EsriErrorHelpers.CreateNotFoundError($"Service '{serviceId}' not found");
            }

            var layer = service.GetLayer(layerId);
            if (layer == null)
            {
                FeatureServerLog.LayerNotFound(_logger, serviceId, layerId);
                return EsriErrorHelpers.CreateNotFoundError($"Layer {layerId} not found in service '{serviceId}'");
            }

            var response = new ApplyEditsResponse();
            var allSuccess = true;

            // Process add operations
            if (request.Adds != null && request.Adds.Length > 0)
            {
                var addResults = await ProcessAddOperationsAsync(layer, request.Adds, cancellationToken);
                response.AddResults = addResults;
                allSuccess = allSuccess && addResults.All(r => r.Success);
            }

            // Process update operations
            if (request.Updates != null && request.Updates.Length > 0)
            {
                var updateResults = await ProcessUpdateOperationsAsync(layer, request.Updates, cancellationToken);
                response.UpdateResults = updateResults;
                allSuccess = allSuccess && updateResults.All(r => r.Success);
            }

            // Process delete operations
            if (request.Deletes != null && request.Deletes.Length > 0)
            {
                var deleteResults = await ProcessDeleteOperationsAsync(layer, request.Deletes, cancellationToken);
                response.DeleteResults = deleteResults;
                allSuccess = allSuccess && deleteResults.All(r => r.Success);
            }

            response.Success = allSuccess;

            FeatureServerLog.ApplyEditsCompleted(_logger, serviceId, layerId, allSuccess);

            return Results.Json(response, FeatureServerJsonContext.Default.ApplyEditsResponse,
                contentType: "application/json");
        }
        catch (Exception ex)
        {
            FeatureServerLog.ApplyEditsFailed(_logger, serviceId, layerId, ex.Message, ex);
            return EsriErrorHelpers.CreateInternalServerError("Apply edits failed", [ex.Message]);
        }
    }

    /// <summary>
    /// Process add operations for new features
    /// </summary>
    private async Task<EditResult[]> ProcessAddOperationsAsync(
        LayerDefinition layer,
        EsriFeature[] features,
        CancellationToken cancellationToken)
    {
        var results = new EditResult[features.Length];

        for (int i = 0; i < features.Length; i++)
        {
            try
            {
                var feature = features[i];

                // Convert Esri geometry to WKB if present
                byte[]? geometry = null;
                if (feature.Geometry != null)
                {
                    var geometryJson = JsonSerializer.Serialize(feature.Geometry, FeatureServerJsonContext.Default.EsriGeometry);
                    geometry = ConvertEsriJsonToWkb(geometryJson);
                }

                // Create feature object
                var newFeature = new Feature
                {
                    Id = 0, // Will be assigned by the database
                    Attributes = (feature.Attributes ?? new Dictionary<string, object?>()).ToImmutableDictionary(),
                    Geometry = geometry
                };

                // Add the feature
                var createdFeature = await _featureStore.CreateAsync(layer.Id, newFeature, cancellationToken);
                var objectId = createdFeature.Id;

                results[i] = new EditResult
                {
                    ObjectId = objectId,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                FeatureServerLog.FeatureAddFailed(_logger, i, ex.Message, ex);
                results[i] = new EditResult
                {
                    Success = false,
                    Error = new EditError
                    {
                        Code = 1000,
                        Description = $"Failed to add feature: {ex.Message}"
                    }
                };
            }
        }

        return results;
    }

    /// <summary>
    /// Process update operations for existing features
    /// </summary>
    private async Task<EditResult[]> ProcessUpdateOperationsAsync(
        LayerDefinition layer,
        EsriFeature[] features,
        CancellationToken cancellationToken)
    {
        var results = new EditResult[features.Length];

        for (int i = 0; i < features.Length; i++)
        {
            try
            {
                var feature = features[i];

                // Extract objectId from attributes
                if (feature.Attributes?.TryGetValue("objectid", out var objectIdObj) != true ||
                    !long.TryParse(objectIdObj?.ToString(), out var objectId))
                {
                    results[i] = new EditResult
                    {
                        Success = false,
                        Error = new EditError
                        {
                            Code = 1001,
                            Description = "ObjectId is required for update operations"
                        }
                    };
                    continue;
                }

                // Convert Esri geometry to WKB if present
                byte[]? geometry = null;
                if (feature.Geometry != null)
                {
                    var geometryJson = JsonSerializer.Serialize(feature.Geometry, FeatureServerJsonContext.Default.EsriGeometry);
                    geometry = ConvertEsriJsonToWkb(geometryJson);
                }

                // Create feature object for update
                var updateFeature = new Feature
                {
                    Id = objectId,
                    Attributes = (feature.Attributes ?? new Dictionary<string, object?>()).ToImmutableDictionary(),
                    Geometry = geometry
                };

                // Update the feature
                await _featureStore.UpdateAsync(layer.Id, updateFeature, cancellationToken);

                results[i] = new EditResult
                {
                    ObjectId = objectId,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                FeatureServerLog.FeatureUpdateFailed(_logger, i, ex.Message, ex);
                results[i] = new EditResult
                {
                    Success = false,
                    Error = new EditError
                    {
                        Code = 1002,
                        Description = $"Failed to update feature: {ex.Message}"
                    }
                };
            }
        }

        return results;
    }

    /// <summary>
    /// Process delete operations for existing features
    /// </summary>
    private async Task<EditResult[]> ProcessDeleteOperationsAsync(
        LayerDefinition layer,
        object[] objectIds,
        CancellationToken cancellationToken)
    {
        var results = new EditResult[objectIds.Length];

        for (int i = 0; i < objectIds.Length; i++)
        {
            try
            {
                if (!long.TryParse(objectIds[i]?.ToString(), out var objectId))
                {
                    results[i] = new EditResult
                    {
                        Success = false,
                        Error = new EditError
                        {
                            Code = 1003,
                            Description = "Invalid ObjectId for delete operation"
                        }
                    };
                    continue;
                }

                // Delete the feature
                await _featureStore.DeleteAsync(layer.Id, objectId, cancellationToken);

                results[i] = new EditResult
                {
                    ObjectId = objectId,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                FeatureServerLog.FeatureDeleteFailed(_logger, i, ex.Message, ex);
                results[i] = new EditResult
                {
                    Success = false,
                    Error = new EditError
                    {
                        Code = 1004,
                        Description = $"Failed to delete feature: {ex.Message}"
                    }
                };
            }
        }

        return results;
    }

    /// <summary>
    /// Applies query limits enforcement and returns validated parameters or null if limits exceeded.
    /// </summary>
    private static QueryParameters? ApplyQueryLimits(QueryParameters queryParams, QueryLimits limits)
    {
        // Validate and apply record count limits
        var recordCount = queryParams.ResultRecordCount ?? limits.DefaultRecordCount;
        if (recordCount > limits.MaxRecordCount)
        {
            return null;
        }

        // Validate offset limits
        var offset = queryParams.ResultOffset ?? 0;
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
                var spatialFilter = ParseSpatialFilter(queryParams.Geometry, queryParams.SpatialRel);
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
        var wkbBytes = ConvertEsriJsonToWkb(geometry);

        // Map Esri spatial relationship to enum
        var relationship = ParseSpatialRelationship(spatialRel);

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
    private byte[] ConvertEsriJsonToWkb(string esriJsonGeometry)
    {
        return _geometryConverter.ConvertEsriJsonToWkb(esriJsonGeometry);
    }

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

}
