// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
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
/// Extension methods to register FeatureServer endpoints
/// </summary>
public static class FeatureServerEndpoints
{
    /// <summary>
    /// Maps FeatureServer REST API endpoints for layer metadata using AOT-compatible routing
    /// </summary>
    public static IEndpointRouteBuilder MapFeatureServerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.Map("/rest/services/{serviceId}/FeatureServer", HandleGetServiceMetadata)
            .WithDisplayName("Get FeatureServer Service Metadata")
            .WithName("GetServiceMetadata")
            .WithSummary("Get FeatureServer service metadata")
            .WithDescription("Returns metadata for a FeatureServer service including all layers")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
        // .Produces<FeatureServerResponse>(200, "application/json")
        // .Produces(404);

        endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}", HandleGetLayerMetadata)
            .WithDisplayName("Get FeatureServer Layer Metadata")
            .WithName("GetLayerMetadata")
            .WithSummary("Get FeatureServer layer metadata")
            .WithDescription("Returns detailed layer metadata for a specific layer")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
        // .Produces<LayerResponse>(200, "application/json")
        // .Produces(404);

        endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}/query", HandleQueryFeaturesGet)
            .WithDisplayName("Query FeatureServer Features (GET)")
            .WithName("QueryFeaturesGet")
            .WithSummary("Query features from a FeatureServer layer using GET")
            .WithDescription("Query features with WHERE clause, spatial filters, and pagination via GET parameters")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
        // .Produces<QueryResponse>(200, "application/json")
        // .Produces(400)
        // .Produces(404);

        endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}/query", HandleQueryFeaturesPost)
            .WithDisplayName("Query FeatureServer Features (POST)")
            .WithName("QueryFeaturesPost")
            .WithSummary("Query features from a FeatureServer layer using POST")
            .WithDescription("Query features with WHERE clause, spatial filters, and pagination via POST body")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
        // .Produces<QueryResponse>(200, "application/json")
        // .Produces(400)
        // .Produces(404);

        return endpoints;
    }

    /// <summary>
    /// Handles service metadata requests
    /// </summary>
    private static async Task HandleGetServiceMetadata(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var serviceId = context.GetRouteValue("serviceId")?.ToString();
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            await WriteErrorAsync(context, "Service ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        var limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();
        var logger = context.RequestServices.GetRequiredService<ILogger<FeatureServerHandler>>();

        var result = await GetServiceMetadataAsync(
            serviceId,
            catalog,
            limitsOptions.Value.Query,
            logger,
            context.RequestAborted);

        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Handles service metadata requests
    /// </summary>
    private static async Task<IResult> GetServiceMetadataAsync(
        string serviceId,
        ILayerCatalog catalog,
        QueryLimits limits,
        ILogger<FeatureServerHandler> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            FeatureServerLog.ServiceMetadataRequested(logger, serviceId);

            var service = await catalog.GetServiceAsync(serviceId, cancellationToken);
            if (service == null)
            {
                FeatureServerLog.ServiceNotFound(logger, serviceId);
                return ErrorResult($"Service '{serviceId}' not found", StatusCodes.Status404NotFound);
            }

            var response = MapServiceToResponse(service, limits);

            FeatureServerLog.ServiceMetadataReturned(logger, serviceId, response.Layers.Length);

            return Results.Json(response, FeatureServerJsonContext.Default.FeatureServerResponse,
                contentType: "application/json");
        }
        catch (Exception ex)
        {
            FeatureServerLog.ServiceMetadataFailed(logger, serviceId, ex.Message, ex);
            return Results.StatusCode(500);
        }
    }

    /// <summary>
    /// Handles layer metadata requests
    /// </summary>
    private static async Task HandleGetLayerMetadata(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var serviceId = context.GetRouteValue("serviceId")?.ToString();
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            await WriteErrorAsync(context, "Service ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (!TryGetRouteInt(context, "layerId", out var layerId))
        {
            await WriteErrorAsync(context, "Layer ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        var limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();
        var logger = context.RequestServices.GetRequiredService<ILogger<FeatureServerHandler>>();

        var result = await GetLayerMetadataAsync(
            serviceId,
            layerId,
            catalog,
            limitsOptions.Value.Query,
            logger,
            context.RequestAborted);

        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Handles layer metadata requests
    /// </summary>
    private static async Task<IResult> GetLayerMetadataAsync(
        string serviceId,
        int layerId,
        ILayerCatalog catalog,
        QueryLimits limits,
        ILogger<FeatureServerHandler> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            FeatureServerLog.LayerMetadataRequested(logger, serviceId, layerId);

            // First check if service exists
            var service = await catalog.GetServiceAsync(serviceId, cancellationToken);
            if (service == null)
            {
                FeatureServerLog.ServiceNotFound(logger, serviceId);
                return ErrorResult($"Service '{serviceId}' not found", StatusCodes.Status404NotFound);
            }

            // Find the layer in the service
            var layer = service.GetLayer(layerId);
            if (layer == null)
            {
                FeatureServerLog.LayerNotFound(logger, serviceId, layerId);
                return ErrorResult($"Layer {layerId} not found in service '{serviceId}'", StatusCodes.Status404NotFound);
            }

            var response = MapLayerToResponse(layer, limits);

            FeatureServerLog.LayerMetadataReturned(logger, serviceId, layerId, layer.Name);

            return Results.Json(response, FeatureServerJsonContext.Default.LayerResponse,
                contentType: "application/json");
        }
        catch (Exception ex)
        {
            FeatureServerLog.LayerMetadataFailed(logger, serviceId, layerId, ex.Message, ex);
            return Results.StatusCode(500);
        }
    }

    /// <summary>
    /// Maps ServiceDefinition to FeatureServerResponse
    /// </summary>
    private static FeatureServerResponse MapServiceToResponse(ServiceDefinition service, QueryLimits queryLimits)
    {
        return new FeatureServerResponse
        {
            ServiceName = service.Name,
            ServiceDescription = service.Description,
            Layers = service.Layers.Select(MapLayerInfo).ToArray(),
            SpatialReference = MapSpatialReference(service.SpatialReference),
            InitialExtent = service.EffectiveExtent.HasValue ? MapExtent(service.EffectiveExtent.Value) : null,
            FullExtent = service.EffectiveExtent.HasValue ? MapExtent(service.EffectiveExtent.Value) : null,
            MaxRecordCount = queryLimits.MaxRecordCount,
            SupportedQueryFormats = service.SupportedFormats,
            Capabilities = string.Join(",", service.Capabilities),
            Fields = service.AllFields.Select(MapFieldInfo).ToArray()
        };
    }

    /// <summary>
    /// Maps LayerDefinition to LayerResponse
    /// </summary>
    private static LayerResponse MapLayerToResponse(LayerDefinition layer, QueryLimits queryLimits)
    {
        var displayField = layer.AttributeFields.FirstOrDefault()?.Name;
        var objectIdField = layer.PrimaryKeyField?.Name ?? "objectid";

        return new LayerResponse
        {
            Id = layer.Id,
            Name = layer.Name,
            Description = layer.Description,
            GeometryType = MapGeometryType(layer.GeometryType),
            SpatialReference = MapSpatialReference(layer.SpatialReference),
            Fields = layer.Fields.Select(MapFieldInfo).ToArray(),
            Extent = layer.Extent.HasValue ? MapExtent(layer.Extent.Value) : null,
            MinScale = layer.MinScale,
            MaxScale = layer.MaxScale,
            DefaultVisibility = layer.DefaultVisibility,
            MaxRecordCount = queryLimits.MaxRecordCount,
            ObjectIdField = objectIdField,
            DisplayField = displayField,
            HasAttachments = false // TODO: Support attachments in future phase
        };
    }

    /// <summary>
    /// Maps LayerDefinition to LayerInfo for service listing
    /// </summary>
    private static LayerInfo MapLayerInfo(LayerDefinition layer)
    {
        return new LayerInfo
        {
            Id = layer.Id,
            Name = layer.Name,
            DefaultVisibility = layer.DefaultVisibility,
            MinScale = layer.MinScale,
            MaxScale = layer.MaxScale,
            GeometryType = MapGeometryType(layer.GeometryType)
        };
    }

    /// <summary>
    /// Maps SpatialReference to SpatialReferenceInfo
    /// </summary>
    private static SpatialReferenceInfo MapSpatialReference(SpatialReference spatialReference)
    {
        return new SpatialReferenceInfo
        {
            Wkid = spatialReference.Srid,
            LatestWkid = spatialReference.Srid, // Use same SRID for both
            Wkt = spatialReference.WellKnownText
        };
    }

    /// <summary>
    /// Maps FeatureExtent to ExtentInfo
    /// </summary>
    private static ExtentInfo MapExtent(FeatureExtent extent)
    {
        return new ExtentInfo
        {
            Xmin = extent.MinX,
            Ymin = extent.MinY,
            Xmax = extent.MaxX,
            Ymax = extent.MaxY,
            SpatialReference = new SpatialReferenceInfo { Wkid = extent.SpatialReference }
        };
    }

    /// <summary>
    /// Maps FieldDefinition to EsriFieldInfo
    /// </summary>
    private static EsriFieldInfo MapFieldInfo(FieldDefinition field)
    {
        var (esriType, sqlType, length) = MapFieldType(field.Type);

        return new EsriFieldInfo
        {
            Name = field.Name,
            Type = esriType,
            Alias = field.Name, // TODO: Support field aliases
            SqlType = sqlType,
            Length = length,
            Nullable = field.Nullable,
            Editable = !field.IsGeometry && !field.Name.Equals("objectid", StringComparison.OrdinalIgnoreCase),
            Visible = true
        };
    }

    /// <summary>
    /// Maps GeometryType to Esri geometry type string
    /// </summary>
    private static string MapGeometryType(GeometryType geometryType)
    {
        return geometryType switch
        {
            GeometryType.Point => "esriGeometryPoint",
            GeometryType.LineString => "esriGeometryPolyline",
            GeometryType.Polygon => "esriGeometryPolygon",
            GeometryType.MultiPoint => "esriGeometryMultipoint",
            GeometryType.MultiLineString => "esriGeometryPolyline",
            GeometryType.MultiPolygon => "esriGeometryPolygon",
            GeometryType.GeometryCollection => "esriGeometryPolygon", // Default to polygon
            GeometryType.None => "esriGeometryNull",
            _ => "esriGeometryPolygon"
        };
    }

    /// <summary>
    /// Maps FieldType to Esri field type, SQL type, and length
    /// </summary>
    private static (string esriType, string sqlType, int? length) MapFieldType(FieldType fieldType)
    {
        return fieldType switch
        {
            FieldType.Integer => ("esriFieldTypeInteger", "sqlTypeInteger", null),
            FieldType.BigInteger => ("esriFieldTypeOID", "sqlTypeBigInt", null),
            FieldType.Float => ("esriFieldTypeSingle", "sqlTypeFloat", null),
            FieldType.Double => ("esriFieldTypeDouble", "sqlTypeDouble", null),
            FieldType.String => ("esriFieldTypeString", "sqlTypeVarchar", 255),
            FieldType.Boolean => ("esriFieldTypeSmallInteger", "sqlTypeSmallInt", null),
            FieldType.Date => ("esriFieldTypeDate", "sqlTypeTimestamp", null),
            FieldType.DateTime => ("esriFieldTypeDate", "sqlTypeTimestamp", null),
            FieldType.Time => ("esriFieldTypeDate", "sqlTypeTimestamp", null),
            FieldType.Geometry => ("esriFieldTypeGeometry", "sqlTypeGeometry", null),
            FieldType.Binary => ("esriFieldTypeBlob", "sqlTypeLongVarBinary", null),
            FieldType.Uuid => ("esriFieldTypeGUID", "sqlTypeOther", 36),
            _ => ("esriFieldTypeString", "sqlTypeVarchar", 255)
        };
    }

    /// <summary>
    /// Handles GET query requests
    /// </summary>
    private static async Task HandleQueryFeaturesGet(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var routeValues = context.Request.RouteValues;
        var serviceId = routeValues["serviceId"]?.ToString();
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            await WriteErrorAsync(context, "Service ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (!TryGetRouteInt(context, "layerId", out var layerId))
        {
            await WriteErrorAsync(context, "Layer ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (!TryBuildQueryParameters(context.Request.Query, out var queryParams, out var error))
        {
            await WriteErrorAsync(context, error ?? "Invalid query parameters", StatusCodes.Status400BadRequest);
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        var featureStore = context.RequestServices.GetRequiredService<IFeatureStore>();
        var geometryConverter = context.RequestServices.GetRequiredService<IGeometryConverter>();
        var queryFormatter = context.RequestServices.GetRequiredService<IQueryFormatter>();
        var limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();
        var logger = context.RequestServices.GetRequiredService<ILogger<FeatureServerHandler>>();

        var result = await QueryFeaturesAsync(
            serviceId,
            layerId,
            queryParams,
            catalog,
            featureStore,
            geometryConverter,
            queryFormatter,
            limitsOptions.Value.Query,
            logger,
            context.RequestAborted);

        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Handles POST query requests
    /// </summary>
    private static async Task HandleQueryFeaturesPost(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var routeValues = context.Request.RouteValues;
        var serviceId = routeValues["serviceId"]?.ToString();
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            await WriteErrorAsync(context, "Service ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (!TryGetRouteInt(context, "layerId", out var layerId))
        {
            await WriteErrorAsync(context, "Layer ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        QueryParameters? queryParams;
        try
        {
            queryParams = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                FeatureServerJsonContext.Default.QueryParameters,
                context.RequestAborted);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(context, "Invalid JSON payload", StatusCodes.Status400BadRequest);
            return;
        }

        if (queryParams is null)
        {
            await WriteErrorAsync(context, "Request body is required", StatusCodes.Status400BadRequest);
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        var featureStore = context.RequestServices.GetRequiredService<IFeatureStore>();
        var geometryConverter = context.RequestServices.GetRequiredService<IGeometryConverter>();
        var queryFormatter = context.RequestServices.GetRequiredService<IQueryFormatter>();
        var limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();
        var logger = context.RequestServices.GetRequiredService<ILogger<FeatureServerHandler>>();

        var result = await QueryFeaturesAsync(
            serviceId,
            layerId,
            queryParams,
            catalog,
            featureStore,
            geometryConverter,
            queryFormatter,
            limitsOptions.Value.Query,
            logger,
            context.RequestAborted);

        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Core query implementation shared by GET and POST endpoints
    /// </summary>
    private static async Task<IResult> QueryFeaturesAsync(
        string serviceId,
        int layerId,
        QueryParameters queryParams,
        ILayerCatalog catalog,
        IFeatureStore featureStore,
        IGeometryConverter geometryConverter,
        IQueryFormatter queryFormatter,
        QueryLimits limits,
        ILogger<FeatureServerHandler> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            FeatureServerLog.QueryRequested(logger, serviceId, layerId, queryParams.Where);

            // Validate service and layer exist
            var service = await catalog.GetServiceAsync(serviceId, cancellationToken);
            if (service == null)
            {
                FeatureServerLog.ServiceNotFound(logger, serviceId);
                return ErrorResult($"Service '{serviceId}' not found", StatusCodes.Status404NotFound);
            }

            var layer = service.GetLayer(layerId);
            if (layer == null)
            {
                FeatureServerLog.LayerNotFound(logger, serviceId, layerId);
                return ErrorResult($"Layer {layerId} not found in service '{serviceId}'", StatusCodes.Status404NotFound);
            }

            if (queryParams.ResultRecordCount is < 1)
            {
                FeatureServerLog.QueryParameterInvalid(logger, "ResultRecordCount", queryParams.ResultRecordCount.Value);
                return ErrorResult("Invalid query parameters", StatusCodes.Status400BadRequest,
                    [$"{nameof(QueryParameters.ResultRecordCount)} must be greater than 0"]);
            }

            if (queryParams.ResultOffset is < 0)
            {
                FeatureServerLog.QueryParameterInvalid(logger, "ResultOffset", queryParams.ResultOffset.Value);
                return ErrorResult("Invalid query parameters", StatusCodes.Status400BadRequest,
                    [$"{nameof(QueryParameters.ResultOffset)} must be 0 or greater"]);
            }

            // Apply limits enforcement
            var validatedParams = ApplyQueryLimits(queryParams, limits, logger);
            if (validatedParams == null)
            {
                return ErrorResult("Query parameters exceed configured limits", StatusCodes.Status400BadRequest,
                    [$"Maximum record count: {limits.MaxRecordCount}", $"Maximum offset: {limits.MaxOffset}"]);
            }

            // Build query from validated parameters
            var query = BuildFeatureQuery(validatedParams, service, geometryConverter);

            // Execute query
            var result = await featureStore.QueryAsync(layerId, query, cancellationToken);

            // Format response using QueryFormatter
            var outFields = string.IsNullOrEmpty(validatedParams.OutFields) ? null :
                validatedParams.OutFields.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(f => f.Trim())
                    .ToArray();

            var (formattedResponse, contentType) = queryFormatter.FormatQueryResult(
                result,
                layer,
                validatedParams.F ?? "json",
                validatedParams.ReturnGeometry,
                outFields);

            FeatureServerLog.QueryCompleted(logger, serviceId, layerId, result.Items.Length, result.TotalCount);

            // Return response with appropriate content type and JSON context
            return validatedParams.F?.ToLowerInvariant() switch
            {
                "geojson" => Results.Json(formattedResponse, FeatureServerJsonContext.Default.GeoJsonFeatureSet, contentType: contentType),
                _ => Results.Json(formattedResponse, FeatureServerJsonContext.Default.QueryResponse, contentType: contentType)
            };
        }
        catch (ArgumentException ex)
        {
            FeatureServerLog.QueryFailed(logger, serviceId, layerId, ex.Message, ex);
            return ErrorResult(ex.Message, StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            FeatureServerLog.QueryFailed(logger, serviceId, layerId, ex.Message, ex);
            return Results.StatusCode(500);
        }
    }

    private static bool TryBuildQueryParameters(IQueryCollection query, out QueryParameters queryParams, out string? error)
    {
        error = null;

        var where = TryGetQueryValue(query, "where");
        var outFields = TryGetQueryValue(query, "outFields");
        var geometry = TryGetQueryValue(query, "geometry");
        var geometryType = TryGetQueryValue(query, "geometryType");
        var spatialRel = TryGetQueryValue(query, "spatialRel");
        var format = TryGetQueryValue(query, "f") ?? "json";

        if (!TryParseQueryBool(query, "returnGeometry", true, out var returnGeometry, out error))
        {
            queryParams = new QueryParameters();
            return false;
        }

        if (!TryParseQueryInt(query, "resultOffset", out var resultOffset, out error))
        {
            queryParams = new QueryParameters();
            return false;
        }

        if (!TryParseQueryInt(query, "resultRecordCount", out var resultRecordCount, out error))
        {
            queryParams = new QueryParameters();
            return false;
        }

        queryParams = new QueryParameters
        {
            Where = where,
            OutFields = outFields,
            ReturnGeometry = returnGeometry,
            F = format,
            ResultOffset = resultOffset,
            ResultRecordCount = resultRecordCount,
            Geometry = geometry,
            GeometryType = geometryType,
            SpatialRel = spatialRel
        };

        return true;
    }

    private static string? TryGetQueryValue(IQueryCollection query, string key)
    {
        if (!query.TryGetValue(key, out var values))
        {
            return null;
        }

        var value = values.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool TryParseQueryInt(IQueryCollection query, string key, out int? value, out string? error)
    {
        value = null;
        error = null;

        var raw = TryGetQueryValue(query, key);
        if (raw is null)
        {
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"{key} must be an integer.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryParseQueryBool(IQueryCollection query, string key, bool defaultValue, out bool value, out string? error)
    {
        value = defaultValue;
        error = null;

        var raw = TryGetQueryValue(query, key);
        if (raw is null)
        {
            return true;
        }

        if (!bool.TryParse(raw, out var parsed))
        {
            error = $"{key} must be a boolean.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryGetRouteInt(HttpContext context, string key, out int value)
    {
        value = default;

        if (!context.Request.RouteValues.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        if (raw is int intValue)
        {
            value = intValue;
            return true;
        }

        return int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static Task WriteErrorAsync(HttpContext context, string message, int statusCode, string[]? details = null)
    {
        var result = ErrorResult(message, statusCode, details);
        return result.ExecuteAsync(context);
    }

    private static IResult ErrorResult(string message, int statusCode, string[]? details = null)
    {
        var error = new ApiErrorResponse
        {
            Error = message,
            Details = details
        };

        return Results.Json(error, FeatureServerJsonContext.Default.ApiErrorResponse, statusCode: statusCode);
    }

    /// <summary>
    /// Builds a FeatureQuery from query parameters
    /// </summary>
    private static FeatureQuery BuildFeatureQuery(QueryParameters queryParams, ServiceDefinition service, IGeometryConverter geometryConverter)
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
            var spatialFilter = ParseSpatialFilter(queryParams.Geometry, queryParams.SpatialRel, geometryConverter);
            query = query with { SpatialFilter = spatialFilter };
        }

        return query;
    }

    /// <summary>
    /// Parses Esri JSON geometry and spatial relationship into a SpatialFilter
    /// </summary>
    private static SpatialFilter ParseSpatialFilter(string geometry, string? spatialRel, IGeometryConverter geometryConverter)
    {
        // Convert Esri JSON geometry to WKB bytes
        var wkbBytes = ConvertEsriJsonToWkb(geometry, geometryConverter);

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
    private static byte[] ConvertEsriJsonToWkb(string esriJsonGeometry, IGeometryConverter geometryConverter)
    {
        return geometryConverter.ConvertEsriJsonToWkb(esriJsonGeometry);
    }


    /// <summary>
    /// Applies query limits enforcement and returns validated parameters or null if limits exceeded.
    /// </summary>
    private static QueryParameters? ApplyQueryLimits(QueryParameters queryParams, QueryLimits limits, ILogger logger)
    {
        // Validate and apply record count limits
        var recordCount = queryParams.ResultRecordCount ?? limits.DefaultRecordCount;
        if (recordCount > limits.MaxRecordCount)
        {
            FeatureServerLog.QueryLimitExceeded(logger, "ResultRecordCount", recordCount, limits.MaxRecordCount);
            return null;
        }

        // Validate offset limits
        var offset = queryParams.ResultOffset ?? 0;
        if (offset > limits.MaxOffset)
        {
            FeatureServerLog.QueryLimitExceeded(logger, "ResultOffset", offset, limits.MaxOffset);
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

}
