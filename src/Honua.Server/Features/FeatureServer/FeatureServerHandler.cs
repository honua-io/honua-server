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
                return Results.NotFound($"Service '{serviceId}' not found");
            }

            var layer = service.Layers.FirstOrDefault(l => l.Id == layerId);
            if (layer == null)
            {
                FeatureServerLog.LayerNotFound(_logger, serviceId, layerId);
                return Results.NotFound($"Layer {layerId} not found in service '{serviceId}'");
            }

            if (queryParams.ResultRecordCount is < 1)
            {
                return Results.Problem(
                    title: "Invalid query parameters",
                    detail: $"{nameof(QueryParameters.ResultRecordCount)} must be greater than 0",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (queryParams.ResultOffset is < 0)
            {
                return Results.Problem(
                    title: "Invalid query parameters",
                    detail: $"{nameof(QueryParameters.ResultOffset)} must be 0 or greater",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Apply limits enforcement
            var validatedParams = ApplyQueryLimits(queryParams, _limitsOptions.Query);
            if (validatedParams == null)
            {
                return Results.Problem(
                    title: "Query parameters exceed configured limits",
                    detail: $"Maximum record count: {_limitsOptions.Query.MaxRecordCount}, Maximum offset: {_limitsOptions.Query.MaxOffset}",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Build query from validated parameters
            var query = BuildFeatureQuery(validatedParams, service);

            // Execute query
            var result = await _featureStore.QueryAsync(layerId, query, cancellationToken);

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
        catch (Exception ex)
        {
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);

            return Results.Problem(
                title: "Query execution failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
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
            var spatialFilter = ParseSpatialFilter(queryParams.Geometry, queryParams.SpatialRel);
            query = query with { SpatialFilter = spatialFilter };
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
}
