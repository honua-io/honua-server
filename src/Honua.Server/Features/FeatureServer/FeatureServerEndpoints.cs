// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

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
        _ = endpoints.Map("/rest/services/{serviceId}/FeatureServer", HandleGetServiceMetadata)
            .WithDisplayName("Get FeatureServer Service Metadata")
            .WithName("GetServiceMetadata")
            .WithSummary("Get FeatureServer service metadata")
            .WithDescription("Returns metadata for a FeatureServer service including all layers")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .CacheOutput("ServiceMetadata");
        // .Produces<FeatureServerResponse>(200, "application/json")
        // .Produces(404);

        _ = endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}", HandleGetLayerMetadata)
            .WithDisplayName("Get FeatureServer Layer Metadata")
            .WithName("GetLayerMetadata")
            .WithSummary("Get FeatureServer layer metadata")
            .WithDescription("Returns detailed layer metadata for a specific layer")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .CacheOutput("LayerMetadata");
        // .Produces<LayerResponse>(200, "application/json")
        // .Produces(404);

        _ = endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}/query", HandleQueryFeaturesGet)
            .WithDisplayName("Query FeatureServer Features (GET)")
            .WithName("QueryFeaturesGet")
            .WithSummary("Query features from a FeatureServer layer using GET")
            .WithDescription("Query features with WHERE clause, spatial filters, and pagination via GET parameters")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
        // .Produces<QueryResponse>(200, "application/json")
        // .Produces(400)
        // .Produces(404);

        _ = endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}/query", HandleQueryFeaturesPost)
            .WithDisplayName("Query FeatureServer Features (POST)")
            .WithName("QueryFeaturesPost")
            .WithSummary("Query features from a FeatureServer layer using POST")
            .WithDescription("Query features with WHERE clause, spatial filters, and pagination via POST body")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
        // .Produces<QueryResponse>(200, "application/json")
        // .Produces(400)
        // .Produces(404);

        endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}/applyEdits", HandleApplyEdits)
            .WithDisplayName("Apply Feature Edits")
            .WithName("ApplyEdits")
            .WithSummary("Apply feature edits (add, update, delete)")
            .WithDescription("Apply feature edits to a layer including add, update, and delete operations")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
        // .Produces<ApplyEditsResponse>(200, "application/json")
        // .Produces(400)
        // .Produces(404);

        _ = endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryRelatedRecords", HandleQueryRelatedRecordsGet)
            .WithDisplayName("Query Related Records (GET)")
            .WithName("QueryRelatedRecordsGet")
            .WithSummary("Query features related to source features through a relationship using GET")
            .WithDescription("Returns features from a related layer based on relationship definitions via GET parameters")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
        // .Produces<QueryRelatedRecordsResponse>(200, "application/json")
        // .Produces(400)
        // .Produces(404);

        _ = endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryRelatedRecords", HandleQueryRelatedRecordsPost)
            .WithDisplayName("Query Related Records (POST)")
            .WithName("QueryRelatedRecordsPost")
            .WithSummary("Query features related to source features through a relationship using POST")
            .WithDescription("Returns features from a related layer based on relationship definitions via POST body")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
        // .Produces<QueryRelatedRecordsResponse>(200, "application/json")
        // .Produces(400)
        // .Produces(404);

        _ = endpoints.Map("/tiles/{layerId:int}/{z:int}/{x:int}/{y:int}.mvt", HandleGetTile)
            .WithDisplayName("Get MVT Tile")
            .WithName("GetMvtTile")
            .WithSummary("Get MVT (Mapbox Vector Tile) for a layer")
            .WithDescription("Generates vector tiles using PostGIS ST_AsMVT with proper clipping and simplification")
            .WithTags("Tiles")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .CacheOutput("MvtTile");
        // .Produces<byte[]>(200, "application/vnd.mapbox-vector-tile")
        // .Produces(204)
        // .Produces(400)
        // .Produces(404);

        return endpoints;
    }

    /// <summary>
    /// Handles service metadata requests
    /// </summary>
    private static async Task HandleGetServiceMetadata(HttpContext context)
    {
        if (!RouteValidationHelpers.ValidateHttpMethod(context, HttpMethods.Get))
            return;

        if (!RouteValidationHelpers.TryValidateServiceId(context, out string? serviceId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Service ID is required");
            return;
        }

        ILayerCatalog catalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        IOptions<LimitsOptions> limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();
        ILoggerFactory loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        ILogger logger = loggerFactory.CreateLogger("Honua.Server.FeatureServerEndpoints");

        IResult result = await GetServiceMetadataAsync(
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
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            FeatureServerLog.ServiceMetadataRequested(logger, serviceId);

            ServiceDefinition? service = await catalog.GetServiceAsync(serviceId, cancellationToken);
            if (service == null)
            {
                FeatureServerLog.ServiceNotFound(logger, serviceId);
                return GeoServicesErrorHelpers.CreateNotFoundError($"Service '{serviceId}' not found");
            }

            FeatureServerResponse response = MapServiceToResponse(service, limits);

            FeatureServerLog.ServiceMetadataReturned(logger, serviceId, response.Layers.Length);

            return Results.Json(response, FeatureServerJsonContext.Default.FeatureServerResponse,
                contentType: "application/json");
        }
        catch (Exception ex)
        {
            FeatureServerLog.ServiceMetadataFailed(logger, serviceId, ex.Message, ex);
            return GeoServicesErrorHelpers.CreateInternalServerError("Service metadata retrieval failed", [ex.Message]);
        }
    }

    /// <summary>
    /// Handles layer metadata requests
    /// </summary>
    private static async Task HandleGetLayerMetadata(HttpContext context)
    {
        if (!RouteValidationHelpers.ValidateHttpMethod(context, HttpMethods.Get))
            return;

        if (!RouteValidationHelpers.TryValidateServiceId(context, out string? serviceId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Service ID is required");
            return;
        }

        if (!RouteValidationHelpers.TryValidateLayerId(context, out int layerId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Layer ID is required");
            return;
        }

        ILayerCatalog catalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        IOptions<LimitsOptions> limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();
        ILoggerFactory loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        ILogger logger = loggerFactory.CreateLogger("Honua.Server.FeatureServerEndpoints");

        IResult result = await GetLayerMetadataAsync(
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
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            FeatureServerLog.LayerMetadataRequested(logger, serviceId, layerId);

            // First check if service exists
            ServiceDefinition? service = await catalog.GetServiceAsync(serviceId, cancellationToken);
            if (service == null)
            {
                FeatureServerLog.ServiceNotFound(logger, serviceId);
                return GeoServicesErrorHelpers.CreateNotFoundError($"Service '{serviceId}' not found");
            }

            // Find the layer in the service
            LayerDefinition? layer = service.GetLayer(layerId);
            if (layer == null)
            {
                FeatureServerLog.LayerNotFound(logger, serviceId, layerId);
                return GeoServicesErrorHelpers.CreateNotFoundError($"Layer {layerId} not found in service '{serviceId}'");
            }

            LayerResponse response = MapLayerToResponse(layer, limits);

            FeatureServerLog.LayerMetadataReturned(logger, serviceId, layerId, layer.Name);

            return Results.Json(response, FeatureServerJsonContext.Default.LayerResponse,
                contentType: "application/json");
        }
        catch (Exception ex)
        {
            FeatureServerLog.LayerMetadataFailed(logger, serviceId, layerId, ex.Message, ex);
            return GeoServicesErrorHelpers.CreateInternalServerError("Layer metadata retrieval failed", [ex.Message]);
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
            Layers = [.. service.Layers.Select(MapLayerInfo)],
            SpatialReference = MapSpatialReference(service.SpatialReference),
            InitialExtent = service.EffectiveExtent.HasValue ? MapExtent(service.EffectiveExtent.Value) : null,
            FullExtent = service.EffectiveExtent.HasValue ? MapExtent(service.EffectiveExtent.Value) : null,
            MaxRecordCount = queryLimits.MaxRecordCount,
            SupportedQueryFormats = service.SupportedFormats,
            Capabilities = string.Join(",", service.Capabilities),
            Fields = [.. service.AllFields.Select(MapFieldInfo)]
        };
    }

    /// <summary>
    /// Maps LayerDefinition to LayerResponse
    /// </summary>
    private static LayerResponse MapLayerToResponse(LayerDefinition layer, QueryLimits queryLimits)
    {
        string? displayField = layer.AttributeFields.FirstOrDefault()?.Name;
        string objectIdField = layer.PrimaryKeyField?.Name ?? "objectid";

        return new LayerResponse
        {
            Id = layer.Id,
            Name = layer.Name,
            Description = layer.Description,
            GeometryType = MapGeometryType(layer.GeometryType),
            SpatialReference = MapSpatialReference(layer.SpatialReference),
            Fields = [.. layer.Fields.Select(MapFieldInfo)],
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
    /// Maps FieldDefinition to GeoServicesFieldInfo
    /// </summary>
    private static GeoServicesFieldInfo MapFieldInfo(FieldDefinition field)
    {
        (string? geoServicesType, string? sqlType, int? length) = MapFieldType(field.Type);

        return new GeoServicesFieldInfo
        {
            Name = field.Name,
            Type = geoServicesType,
            Alias = field.Name, // TODO: Support field aliases
            SqlType = sqlType,
            Length = length,
            Nullable = field.Nullable,
            Editable = !field.IsGeometry && !field.Name.Equals("objectid", StringComparison.OrdinalIgnoreCase),
            Visible = true
        };
    }

    /// <summary>
    /// Maps GeometryType to GeoServices geometry type string
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
    /// Maps FieldType to GeoServices field type, SQL type, and length
    /// </summary>
    private static (string geoServicesType, string sqlType, int? length) MapFieldType(FieldType fieldType)
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
        if (!RouteValidationHelpers.ValidateHttpMethod(context, HttpMethods.Get))
            return;

        if (!RouteValidationHelpers.TryValidateServiceId(context, out string? serviceId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Service ID is required");
            return;
        }

        if (!RouteValidationHelpers.TryValidateLayerId(context, out int layerId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Layer ID is required");
            return;
        }

        if (!TryBuildQueryParameters(context.Request.Query, out QueryParameters? queryParams, out string? error))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, error ?? "Invalid query parameters");
            return;
        }

        FeatureServerHandler handler = context.RequestServices.GetRequiredService<FeatureServerHandler>();

        IResult result = await handler.HandleQueryFeaturesAsync(
            serviceId,
            layerId,
            queryParams,
            GetTimeoutAwareCancellationToken(context));

        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Handles POST query requests
    /// </summary>
    private static async Task HandleQueryFeaturesPost(HttpContext context)
    {
        if (!RouteValidationHelpers.ValidateHttpMethod(context, HttpMethods.Post))
            return;

        if (!RouteValidationHelpers.TryValidateServiceId(context, out string? serviceId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Service ID is required");
            return;
        }

        if (!RouteValidationHelpers.TryValidateLayerId(context, out int layerId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Layer ID is required");
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
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Invalid JSON payload");
            return;
        }

        if (queryParams is null)
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Request body is required");
            return;
        }

        FeatureServerHandler handler = context.RequestServices.GetRequiredService<FeatureServerHandler>();

        IResult result = await handler.HandleQueryFeaturesAsync(
            serviceId,
            layerId,
            queryParams,
            GetTimeoutAwareCancellationToken(context));

        await result.ExecuteAsync(context);
    }


    private static bool TryBuildQueryParameters(IQueryCollection query, out QueryParameters queryParams, out string? error)
    {
        string? where = TryGetQueryValue(query, "where");
        string? outFields = TryGetQueryValue(query, "outFields");
        string? geometry = TryGetQueryValue(query, "geometry");
        string? geometryType = TryGetQueryValue(query, "geometryType");
        string? spatialRel = TryGetQueryValue(query, "spatialRel");
        string? units = TryGetQueryValue(query, "units");
        string format = TryGetQueryValue(query, "f") ?? "json";

        if (!TryParseQueryBool(query, "returnGeometry", true, out bool returnGeometry, out error))
        {
            queryParams = new QueryParameters();
            return false;
        }

        if (!TryParseQueryBool(query, "returnDistance", false, out bool returnDistance, out error))
        {
            queryParams = new QueryParameters();
            return false;
        }

        if (!TryParseQueryInt(query, "resultOffset", out int? resultOffset, out error))
        {
            queryParams = new QueryParameters();
            return false;
        }

        if (!TryParseQueryInt(query, "resultRecordCount", out int? resultRecordCount, out error))
        {
            queryParams = new QueryParameters();
            return false;
        }

        if (!TryParseQueryInt(query, "nearestCount", out int? nearestCount, out error))
        {
            queryParams = new QueryParameters();
            return false;
        }

        if (!TryParseQueryDouble(query, "distance", out double? distance, out error))
        {
            queryParams = new QueryParameters();
            return false;
        }

        queryParams = new QueryParameters
        {
            Where = where,
            OutFields = outFields,
            ReturnGeometry = returnGeometry,
            ReturnDistance = returnDistance,
            F = format,
            ResultOffset = resultOffset,
            ResultRecordCount = resultRecordCount,
            NearestCount = nearestCount,
            Geometry = geometry,
            GeometryType = geometryType,
            SpatialRel = spatialRel,
            Distance = distance,
            Units = units
        };

        return true;
    }

    private static string? TryGetQueryValue(IQueryCollection query, string key)
    {
        if (!query.TryGetValue(key, out StringValues values))
        {
            return null;
        }

        string value = values.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool TryParseQueryInt(IQueryCollection query, string key, out int? value, out string? error)
    {
        value = null;
        error = null;

        string? raw = TryGetQueryValue(query, key);
        if (raw is null)
        {
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
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

        string? raw = TryGetQueryValue(query, key);
        if (raw is null)
        {
            return true;
        }

        if (!bool.TryParse(raw, out bool parsed))
        {
            error = $"{key} must be a boolean.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryParseQueryDouble(IQueryCollection query, string key, out double? value, out string? error)
    {
        value = null;
        error = null;

        string? raw = TryGetQueryValue(query, key);
        if (raw is null)
        {
            return true;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            error = $"{key} must be a number.";
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// Handles applyEdits requests
    /// </summary>
    private static async Task HandleApplyEdits(HttpContext context)
    {
        if (!RouteValidationHelpers.ValidateHttpMethod(context, HttpMethods.Post))
            return;

        if (!RouteValidationHelpers.TryValidateServiceId(context, out var serviceId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Service ID is required");
            return;
        }

        if (!RouteValidationHelpers.TryValidateLayerId(context, out var layerId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Layer ID is required");
            return;
        }

        ApplyEditsRequest? editsRequest;
        try
        {
            editsRequest = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                FeatureServerJsonContext.Default.ApplyEditsRequest,
                context.RequestAborted);
        }
        catch (JsonException)
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Invalid JSON payload");
            return;
        }

        if (editsRequest is null)
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Request body is required");
            return;
        }

        var handler = context.RequestServices.GetRequiredService<FeatureServerHandler>();

        var result = await handler.HandleApplyEditsAsync(
            serviceId,
            layerId,
            editsRequest,
            context.RequestAborted);

        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Handles GET query related records requests
    /// </summary>
    private static async Task HandleQueryRelatedRecordsGet(HttpContext context)
    {
        if (!RouteValidationHelpers.ValidateHttpMethod(context, HttpMethods.Get))
            return;

        if (!RouteValidationHelpers.TryValidateServiceId(context, out string? serviceId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Service ID is required");
            return;
        }

        if (!RouteValidationHelpers.TryValidateLayerId(context, out int layerId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Layer ID is required");
            return;
        }

        // Parse required parameters from query string
        if (!TryBuildRelatedRecordsParameters(context, out QueryRelatedRecordsParameters? relatedParams, out string? error))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, error ?? "Invalid related records parameters");
            return;
        }

        FeatureServerHandler handler = context.RequestServices.GetRequiredService<FeatureServerHandler>();

        IResult result = await handler.HandleQueryRelatedRecordsAsync(
            serviceId,
            layerId,
            relatedParams,
            context.RequestAborted);

        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Handles POST query related records requests
    /// </summary>
    private static async Task HandleQueryRelatedRecordsPost(HttpContext context)
    {
        if (!RouteValidationHelpers.ValidateHttpMethod(context, HttpMethods.Post))
            return;

        if (!RouteValidationHelpers.TryValidateServiceId(context, out string? serviceId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Service ID is required");
            return;
        }

        if (!RouteValidationHelpers.TryValidateLayerId(context, out int layerId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Layer ID is required");
            return;
        }

        // Parse required parameters from POST body
        (bool success, QueryRelatedRecordsParameters? relatedParams, string? error) = await TryBuildRelatedRecordsParametersFromJsonAsync(context);
        if (!success)
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, error ?? "Invalid related records parameters");
            return;
        }

        if (relatedParams is null)
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Failed to parse related records parameters");
            return;
        }

        FeatureServerHandler handler = context.RequestServices.GetRequiredService<FeatureServerHandler>();

        IResult result = await handler.HandleQueryRelatedRecordsAsync(
            serviceId,
            layerId,
            relatedParams,
            context.RequestAborted);

        await result.ExecuteAsync(context);
    }

    private static bool TryBuildRelatedRecordsParameters(HttpContext context, out QueryRelatedRecordsParameters parameters, out string? error)
    {
        error = null;
        parameters = null!; // Will be set later once we have all required values

        IQueryCollection query = context.Request.Query;

        // Required: objectIds
        string? objectIdsValue = TryGetQueryValue(query, "objectIds");
        if (string.IsNullOrWhiteSpace(objectIdsValue))
        {
            error = "objectIds parameter is required";
            return false;
        }

        string[] objectIdStrings = objectIdsValue.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var objectIds = new List<long>();

        foreach (string idString in objectIdStrings)
        {
            if (long.TryParse(idString.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
            {
                objectIds.Add(id);
            }
            else
            {
                error = $"Invalid objectId: {idString}";
                return false;
            }
        }

        if (objectIds.Count == 0)
        {
            error = "At least one valid objectId is required";
            return false;
        }

        // Required: relationshipId
        if (!TryParseQueryInt(query, "relationshipId", out int? relationshipId, out string? relationshipIdError))
        {
            error = relationshipIdError ?? "relationshipId parameter is required";
            return false;
        }

        if (!relationshipId.HasValue)
        {
            error = "relationshipId parameter is required";
            return false;
        }

        // Optional parameters
        string? outFields = TryGetQueryValue(query, "outFields");
        string? where = TryGetQueryValue(query, "where");
        string format = TryGetQueryValue(query, "f") ?? "json";

        if (!TryParseQueryBool(query, "returnGeometry", true, out bool returnGeometry, out error))
        {
            return false;
        }

        if (!TryParseQueryInt(query, "resultOffset", out int? resultOffset, out error))
        {
            return false;
        }

        if (!TryParseQueryInt(query, "resultRecordCount", out int? resultRecordCount, out error))
        {
            return false;
        }

        parameters = new QueryRelatedRecordsParameters
        {
            ObjectIds = [.. objectIds],
            RelationshipId = relationshipId.Value,
            OutFields = outFields,
            Where = where,
            ReturnGeometry = returnGeometry,
            F = format,
            ResultOffset = resultOffset,
            ResultRecordCount = resultRecordCount
        };

        return true;
    }

    /// <summary>
    /// Builds QueryRelatedRecordsParameters from JSON POST body
    /// </summary>
    private static async Task<(bool Success, QueryRelatedRecordsParameters? Parameters, string? Error)> TryBuildRelatedRecordsParametersFromJsonAsync(
        HttpContext context)
    {

        try
        {
            // First, try to deserialize as a generic JSON object to handle string-based objectIds
            using var jsonDoc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            JsonElement root = jsonDoc.RootElement;

            // Extract and parse objectIds (can be string or array)
            if (!root.TryGetProperty("objectIds", out JsonElement objectIdsElement))
            {
                return (false, null, "objectIds parameter is required");
            }

            List<long> objectIds = [];
            if (objectIdsElement.ValueKind == JsonValueKind.String)
            {
                // Handle string format "1,2,3"
                string objectIdsString = objectIdsElement.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(objectIdsString))
                {
                    return (false, null, "objectIds parameter is required");
                }

                string[] objectIdStrings = objectIdsString.Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (string idString in objectIdStrings)
                {
                    if (long.TryParse(idString.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
                    {
                        objectIds.Add(id);
                    }
                    else
                    {
                        return (false, null, $"Invalid objectId: {idString}");
                    }
                }
            }
            else if (objectIdsElement.ValueKind == JsonValueKind.Array)
            {
                // Handle array format [1, 2, 3]
                foreach (JsonElement element in objectIdsElement.EnumerateArray())
                {
                    if (element.TryGetInt64(out long id))
                    {
                        objectIds.Add(id);
                    }
                    else
                    {
                        return (false, null, "Invalid objectId in array");
                    }
                }
            }
            else
            {
                return (false, null, "objectIds must be a string or array");
            }

            if (objectIds.Count == 0)
            {
                return (false, null, "At least one valid objectId is required");
            }

            // Extract relationshipId
            if (!root.TryGetProperty("relationshipId", out JsonElement relationshipIdElement) ||
                !relationshipIdElement.TryGetInt32(out int relationshipId))
            {
                return (false, null, "relationshipId parameter is required and must be an integer");
            }

            // Extract optional parameters
            string? outFields = null;
            if (root.TryGetProperty("outFields", out JsonElement outFieldsElement))
            {
                outFields = outFieldsElement.GetString();
            }

            string? where = null;
            if (root.TryGetProperty("where", out JsonElement whereElement))
            {
                where = whereElement.GetString();
            }

            bool returnGeometry = true; // Default value
            if (root.TryGetProperty("returnGeometry", out JsonElement returnGeometryElement))
            {
                if (returnGeometryElement.ValueKind == JsonValueKind.True)
                {
                    returnGeometry = true;
                }
                else if (returnGeometryElement.ValueKind == JsonValueKind.False)
                {
                    returnGeometry = false;
                }
                else if (returnGeometryElement.ValueKind == JsonValueKind.String)
                {
                    string returnGeomStr = returnGeometryElement.GetString() ?? "";
                    returnGeometry = !returnGeomStr.Equals("false", StringComparison.OrdinalIgnoreCase);
                }
            }

            int? resultRecordCount = null;
            if (root.TryGetProperty("resultRecordCount", out JsonElement resultRecordCountElement) &&
                resultRecordCountElement.TryGetInt32(out int recordCount))
            {
                resultRecordCount = recordCount;
            }

            int? resultOffset = null;
            if (root.TryGetProperty("resultOffset", out JsonElement resultOffsetElement) &&
                resultOffsetElement.TryGetInt32(out int offset))
            {
                resultOffset = offset;
            }

            var parameters = new QueryRelatedRecordsParameters
            {
                ObjectIds = [.. objectIds],
                RelationshipId = relationshipId,
                OutFields = outFields,
                Where = where,
                ReturnGeometry = returnGeometry,
                ResultRecordCount = resultRecordCount,
                ResultOffset = resultOffset
            };

            return (true, parameters, null);
        }
        catch (JsonException)
        {
            return (false, null, "Invalid JSON payload");
        }
        catch (Exception ex)
        {
            return (false, null, $"Failed to parse request: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles MVT tile requests
    /// </summary>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="z">Zoom level</param>
    /// <param name="x">Tile X coordinate</param>
    /// <param name="y">Tile Y coordinate</param>
    /// <param name="where">Optional WHERE clause for filtering</param>
    /// <param name="response">HTTP response for setting headers</param>
    /// <param name="featureStore">Feature store service</param>
    /// <param name="layerCatalog">Layer catalog service</param>
    /// <param name="tileOptions">Tile configuration options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>MVT tile data or 204 if empty</returns>
    private static async Task<IResult> HandleGetTile(
        int layerId,
        int z,
        int x,
        int y,
        string? where,
        HttpResponse response,
        IFeatureStore featureStore,
        ILayerCatalog layerCatalog,
        IOptions<TileOptions> tileOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            // Validate tile configuration
            var options = tileOptions.Value;
            if (z < options.MinZoom || z > options.MaxZoom)
            {
                return Results.BadRequest($"Zoom level {z} is outside supported range ({options.MinZoom}-{options.MaxZoom})");
            }

            // Validate tile coordinates
            if (!TileMath.ValidateTileCoordinates(x, y, z))
            {
                return Results.BadRequest($"Invalid tile coordinates: x={x}, y={y}, z={z}");
            }

            // Verify layer exists
            var layer = await layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                return Results.NotFound($"Layer {layerId} not found");
            }

            // Create feature query with optional WHERE clause
            var query = new FeatureQuery
            {
                Where = where
            };

            // Generate MVT tile using TileOptions
            var mvtData = await featureStore.GetMvtTileAsync(layerId, x, y, z, query, options, cancellationToken);

            if (mvtData == null || mvtData.Length == 0)
            {
                // Return 204 No Content for empty tiles
                return Results.NoContent();
            }

            // Return MVT with appropriate content type
            response.Headers["Cache-Control"] = $"public, max-age={options.CacheMaxAge}";
            return Results.Bytes(mvtData, "application/vnd.mapbox-vector-tile");
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: 500,
                title: "Tile generation failed");
        }
    }

    /// <summary>
    /// Gets the timeout-aware cancellation token from middleware, falling back to request cancellation token
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>Cancellation token that respects timeout limits</returns>
    private static CancellationToken GetTimeoutAwareCancellationToken(HttpContext context)
    {
        // Try to get the timeout token from LimitsEnforcementMiddleware
        if (context.Items.TryGetValue("LimitsTimeoutToken", out var tokenObj) && tokenObj is CancellationToken timeoutToken)
        {
            return timeoutToken;
        }

        // Fallback to request cancellation token
        return context.RequestAborted;
    }
}
