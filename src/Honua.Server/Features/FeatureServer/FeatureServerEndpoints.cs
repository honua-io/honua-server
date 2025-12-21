// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Helpers;
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
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .CacheOutput("ServiceMetadata");
        // .Produces<FeatureServerResponse>(200, "application/json")
        // .Produces(404);

        endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}", HandleGetLayerMetadata)
            .WithDisplayName("Get FeatureServer Layer Metadata")
            .WithName("GetLayerMetadata")
            .WithSummary("Get FeatureServer layer metadata")
            .WithDescription("Returns detailed layer metadata for a specific layer")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .CacheOutput("LayerMetadata");
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
        if (!RouteValidationHelpers.ValidateHttpMethod(context, HttpMethods.Get))
            return;

        if (!RouteValidationHelpers.TryValidateServiceId(context, out var serviceId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Service ID is required");
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
                return EsriErrorHelpers.CreateNotFoundError($"Service '{serviceId}' not found");
            }

            var response = MapServiceToResponse(service, limits);

            FeatureServerLog.ServiceMetadataReturned(logger, serviceId, response.Layers.Length);

            return Results.Json(response, FeatureServerJsonContext.Default.FeatureServerResponse,
                contentType: "application/json");
        }
        catch (Exception ex)
        {
            FeatureServerLog.ServiceMetadataFailed(logger, serviceId, ex.Message, ex);
            return EsriErrorHelpers.CreateInternalServerError("Service metadata retrieval failed", [ex.Message]);
        }
    }

    /// <summary>
    /// Handles layer metadata requests
    /// </summary>
    private static async Task HandleGetLayerMetadata(HttpContext context)
    {
        if (!RouteValidationHelpers.ValidateHttpMethod(context, HttpMethods.Get))
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
                return EsriErrorHelpers.CreateNotFoundError($"Service '{serviceId}' not found");
            }

            // Find the layer in the service
            var layer = service.GetLayer(layerId);
            if (layer == null)
            {
                FeatureServerLog.LayerNotFound(logger, serviceId, layerId);
                return EsriErrorHelpers.CreateNotFoundError($"Layer {layerId} not found in service '{serviceId}'");
            }

            var response = MapLayerToResponse(layer, limits);

            FeatureServerLog.LayerMetadataReturned(logger, serviceId, layerId, layer.Name);

            return Results.Json(response, FeatureServerJsonContext.Default.LayerResponse,
                contentType: "application/json");
        }
        catch (Exception ex)
        {
            FeatureServerLog.LayerMetadataFailed(logger, serviceId, layerId, ex.Message, ex);
            return EsriErrorHelpers.CreateInternalServerError("Layer metadata retrieval failed", [ex.Message]);
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
        if (!RouteValidationHelpers.ValidateHttpMethod(context, HttpMethods.Get))
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

        if (!TryBuildQueryParameters(context.Request.Query, out var queryParams, out var error))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, error ?? "Invalid query parameters");
            return;
        }

        var handler = context.RequestServices.GetRequiredService<FeatureServerHandler>();

        var result = await handler.HandleQueryFeaturesAsync(
            serviceId,
            layerId,
            queryParams,
            context.RequestAborted);

        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Handles POST query requests
    /// </summary>
    private static async Task HandleQueryFeaturesPost(HttpContext context)
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

        var handler = context.RequestServices.GetRequiredService<FeatureServerHandler>();

        var result = await handler.HandleQueryFeaturesAsync(
            serviceId,
            layerId,
            queryParams,
            context.RequestAborted);

        await result.ExecuteAsync(context);
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


}
