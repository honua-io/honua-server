// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
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
internal static partial class FeatureServerEndpoints
{
    internal sealed class FeatureServerEndpointsLog
    {
    }

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

        _ = endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}/generateRenderer", HandleGenerateRenderer)
            .WithDisplayName("Generate Renderer")
            .WithName("GenerateRenderer")
            .WithSummary("Generate a renderer for a FeatureServer layer")
            .WithDescription("Generates a renderer definition based on classification parameters")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
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
            GetTimeoutAwareCancellationToken(context));

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
            return GeoServicesErrorHelpers.CreateInternalServerError("Service metadata retrieval failed");
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
        IFeatureStore featureStore = context.RequestServices.GetRequiredService<IFeatureStore>();
        IOptions<LimitsOptions> limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();
        ILoggerFactory loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        ILogger logger = loggerFactory.CreateLogger("Honua.Server.FeatureServerEndpoints");

        IResult result = await GetLayerMetadataAsync(
            serviceId,
            layerId,
            catalog,
            featureStore,
            limitsOptions.Value.Query,
            logger,
            GetTimeoutAwareCancellationToken(context));

        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Handles layer metadata requests
    /// </summary>
    private static async Task<IResult> GetLayerMetadataAsync(
        string serviceId,
        int layerId,
        ILayerCatalog catalog,
        IFeatureStore featureStore,
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

            FeatureExtent? extent = layer.Extent;
            if (!extent.HasValue)
            {
                extent = await featureStore.GetExtentAsync(layerId, cancellationToken: cancellationToken);
            }

            if (!extent.HasValue)
            {
                extent = FeatureExtent.Create(-180, -90, 180, 90, layer.SpatialReference.Srid);
            }

            LayerResponse response = MapLayerToResponse(layer, limits, extent);

            FeatureServerLog.LayerMetadataReturned(logger, serviceId, layerId, layer.Name);

            return Results.Json(response, FeatureServerJsonContext.Default.LayerResponse,
                contentType: "application/json");
        }
        catch (Exception ex)
        {
            FeatureServerLog.LayerMetadataFailed(logger, serviceId, layerId, ex.Message, ex);
            return GeoServicesErrorHelpers.CreateInternalServerError("Layer metadata retrieval failed");
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
    private static LayerResponse MapLayerToResponse(LayerDefinition layer, QueryLimits queryLimits, FeatureExtent? extent)
    {
        string? displayField = layer.AttributeFields.FirstOrDefault()?.Name;
        string objectIdField = layer.PrimaryKeyField?.Name ?? "objectid";

        var effectiveExtent = extent ?? layer.Extent;

        return new LayerResponse
        {
            Id = layer.Id,
            Name = layer.Name,
            Description = layer.Description,
            GeometryType = MapGeometryType(layer.GeometryType),
            SpatialReference = MapSpatialReference(layer.SpatialReference),
            Fields = [.. layer.Fields.Select(MapFieldInfo)],
            Extent = effectiveExtent.HasValue ? MapExtent(effectiveExtent.Value) : null,
            MinScale = layer.MinScale,
            MaxScale = layer.MaxScale,
            DefaultVisibility = layer.DefaultVisibility,
            MaxRecordCount = queryLimits.MaxRecordCount,
            ObjectIdField = objectIdField,
            DisplayField = displayField,
            HasAttachments = true // Attachments are supported (implemented in Phase 2)
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
            Alias = field.DisplayName, // Use DisplayName which provides alias functionality
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

        var (queryParams, parseError) = await TryParseQueryParametersAsync(context);
        if (parseError is not null)
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, parseError);
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

    private static async Task HandleGenerateRenderer(HttpContext context)
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

        var catalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);

        var service = await catalog.GetServiceAsync(serviceId, cancellationToken);
        if (service == null)
        {
            await GeoServicesErrorHelpers.CreateNotFoundError($"Service '{serviceId}' not found")
                .ExecuteAsync(context);
            return;
        }

        if (service.GetLayer(layerId) == null)
        {
            await GeoServicesErrorHelpers.CreateNotFoundError(
                $"Layer {layerId} not found in service '{serviceId}'").ExecuteAsync(context);
            return;
        }

        if (!context.Request.Query.TryGetValue("classificationDef", out var classificationDef) ||
            StringValues.IsNullOrEmpty(classificationDef))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "classificationDef is required");
            return;
        }

        try
        {
            _ = JsonDocument.Parse(classificationDef.ToString());
        }
        catch (JsonException)
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "classificationDef must be valid JSON");
            return;
        }

        await GeoServicesErrorHelpers.CreateBadRequestError("generateRenderer is not implemented")
            .ExecuteAsync(context);
    }

    private static async Task<(QueryParameters? Parameters, string? Error)> TryParseQueryParametersAsync(HttpContext context)
    {
        context.Request.EnableBuffering();

        if (IsFormContentType(context.Request))
        {
            IFormCollection form;
            try
            {
                form = await context.Request.ReadFormAsync(context.RequestAborted);
            }
            catch (InvalidDataException)
            {
                return (null, "Invalid form payload");
            }

            if (form.Count > 0)
            {
                var formValues = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in form)
                {
                    formValues[entry.Key] = entry.Value;
                }

                if (!TryBuildQueryParameters(new QueryCollection(formValues), out var queryParams, out var formError))
                {
                    return (null, formError);
                }

                return (queryParams, null);
            }

            if (context.Request.Body.CanSeek)
            {
                context.Request.Body.Position = 0;
            }
        }

        string body;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync(context.RequestAborted);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, "Request body is required");
        }

        var trimmedBody = body.TrimStart();
        if (trimmedBody.StartsWith('{') || trimmedBody.StartsWith('['))
        {
            try
            {
                var queryParams = JsonSerializer.Deserialize(
                    trimmedBody,
                    FeatureServerJsonContext.Default.QueryParameters);
                return (queryParams, queryParams == null ? "Request body is required" : null);
            }
            catch (JsonException)
            {
                return (null, "Invalid JSON payload");
            }
        }

        var queryString = trimmedBody.StartsWith('?') ? trimmedBody : $"?{trimmedBody}";
        var values = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(queryString);
        if (!TryBuildQueryParameters(new QueryCollection(values), out var parsedParams, out var error))
        {
            return (null, error);
        }

        return (parsedParams, null);
    }


    private static bool TryBuildQueryParameters(IQueryCollection query, out QueryParameters queryParams, out string? error)
    {
        string? where = TryGetQueryValue(query, "where");
        string? outFields = TryGetQueryValue(query, "outFields");
        string? orderByFields = TryGetQueryValue(query, "orderByFields");
        string? geometry = TryGetQueryValue(query, "geometry");
        string? inSr = TryGetQueryValue(query, "inSR");
        string? outSr = TryGetQueryValue(query, "outSR");
        string? geometryType = TryGetQueryValue(query, "geometryType");
        string? spatialRel = TryGetQueryValue(query, "spatialRel");
        string? units = TryGetQueryValue(query, "units");
        string format = TryGetQueryValue(query, "f") ?? "json";

        if (!TryParseQueryBool(query, "returnGeometry", true, out bool returnGeometry, out error))
        {
            queryParams = new QueryParameters();
            return false;
        }

        if (!TryParseQueryBool(query, "returnIdsOnly", false, out bool returnIdsOnly, out error))
        {
            queryParams = new QueryParameters();
            return false;
        }

        if (!TryParseQueryBool(query, "returnCountOnly", false, out bool returnCountOnly, out error))
        {
            queryParams = new QueryParameters();
            return false;
        }

        if (!TryParseQueryBool(query, "returnExtentOnly", false, out bool returnExtentOnly, out error))
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
            OrderByFields = orderByFields,
            ReturnGeometry = returnGeometry,
            ReturnIdsOnly = returnIdsOnly,
            ReturnCountOnly = returnCountOnly,
            ReturnExtentOnly = returnExtentOnly,
            ReturnDistance = returnDistance,
            F = format,
            ResultOffset = resultOffset,
            ResultRecordCount = resultRecordCount,
            NearestCount = nearestCount,
            Geometry = geometry,
            InSr = inSr,
            OutSr = outSr,
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

        var (editsRequest, parseError) = await TryParseApplyEditsRequestAsync(context);
        if (parseError is not null)
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, parseError);
            return;
        }

        if (editsRequest is null)
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Request body is required");
            return;
        }

        var handler = context.RequestServices.GetRequiredService<FeatureServerHandler>();
        var limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();

        var result = await handler.HandleApplyEditsAsync(
            serviceId,
            layerId,
            editsRequest,
            limitsOptions.Value.Edits,
            GetTimeoutAwareCancellationToken(context));

        await result.ExecuteAsync(context);
    }

    private static async Task<(ApplyEditsRequest? Request, string? Error)> TryParseApplyEditsRequestAsync(
        HttpContext context)
    {
        string? error;

        context.Request.EnableBuffering();

        if (IsFormContentType(context.Request))
        {
            IFormCollection form;
            try
            {
                form = await context.Request.ReadFormAsync(context.RequestAborted);
            }
            catch (InvalidDataException)
            {
                return (null, "Invalid form payload");
            }

            if (form.Count > 0 && HasApplyEditsKeys(form))
            {
                if (!TryParseApplyEditsForm(form, out var request, out error))
                {
                    return (null, error);
                }

                return (request, null);
            }

            if (context.Request.Body.CanSeek)
            {
                context.Request.Body.Position = 0;
            }
        }

        string body;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync(context.RequestAborted);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        var trimmedBody = body.TrimStart();
        if (!trimmedBody.StartsWith('{') && !trimmedBody.StartsWith('['))
        {
            var queryString = trimmedBody.StartsWith('?') ? trimmedBody : $"?{trimmedBody}";
            var values = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(queryString);
            if (values.Count > 0)
            {
                var form = new FormCollection(values);
                if (HasApplyEditsKeys(form))
                {
                    if (!TryParseApplyEditsForm(form, out var request, out error))
                    {
                        return (null, error);
                    }

                    return (request, null);
                }
            }
        }

        try
        {
            var request = JsonSerializer.Deserialize(
                body,
                FeatureServerJsonContext.Default.ApplyEditsRequest);
            return (request, null);
        }
        catch (JsonException)
        {
            return (null, "Invalid JSON payload");
        }
    }

    private static bool IsFormContentType(HttpRequest request)
    {
        if (request.HasFormContentType)
        {
            return true;
        }

        var contentType = request.ContentType;
        return !string.IsNullOrWhiteSpace(contentType) &&
               contentType.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasApplyEditsKeys(IFormCollection form)
        => form.ContainsKey("adds") || form.ContainsKey("updates") || form.ContainsKey("deletes") ||
           form.ContainsKey("rollbackOnFailure") || form.ContainsKey("useGlobalIds") || form.ContainsKey("f");

    private static bool TryParseApplyEditsForm(
        IFormCollection form,
        out ApplyEditsRequest? request,
        out string? error)
    {
        request = null;
        error = null;

        if (!TryParseGeoServicesFeatures(form, "adds", out var adds, out error))
        {
            return false;
        }

        if (!TryParseGeoServicesFeatures(form, "updates", out var updates, out error))
        {
            return false;
        }

        if (!TryParseDeleteIds(form, "deletes", out var deletes, out error))
        {
            return false;
        }

        var rollbackOnFailure = false;
        if (!TryParseFormBool(form, "rollbackOnFailure", out rollbackOnFailure, out error))
        {
            return false;
        }

        var useGlobalIds = false;
        if (!TryParseFormBool(form, "useGlobalIds", out useGlobalIds, out error))
        {
            return false;
        }

        request = new ApplyEditsRequest
        {
            Adds = adds,
            Updates = updates,
            Deletes = deletes,
            RollbackOnFailure = rollbackOnFailure,
            UseGlobalIds = useGlobalIds
        };

        return true;
    }

    private static bool TryParseGeoServicesFeatures(
        IFormCollection form,
        string key,
        out GeoServicesFeature[]? features,
        out string? error)
    {
        features = null;
        error = null;

        if (!form.TryGetValue(key, out var rawValue))
        {
            return true;
        }

        var raw = rawValue.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var trimmed = raw.Trim();
        try
        {
            if (trimmed.StartsWith('['))
            {
                features = JsonSerializer.Deserialize(trimmed, FeatureServerJsonContext.Default.GeoServicesFeatureArray);
            }
            else
            {
                var feature = JsonSerializer.Deserialize(trimmed, FeatureServerJsonContext.Default.GeoServicesFeature);
                features = feature == null ? null : new[] { feature };
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid {key} JSON payload: {ex.Message}";
            return false;
        }
    }

    private static bool TryParseDeleteIds(
        IFormCollection form,
        string key,
        out object[]? deletes,
        out string? error)
    {
        deletes = null;
        error = null;

        if (!form.TryGetValue(key, out var rawValue))
        {
            return true;
        }

        var raw = rawValue.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    error = $"{key} must be a JSON array";
                    return false;
                }

                var values = new List<object>();
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var longValue))
                    {
                        values.Add(longValue);
                        continue;
                    }

                    if (element.ValueKind == JsonValueKind.String)
                    {
                        var text = element.GetString();
                        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        {
                            values.Add(parsed);
                        }
                        else if (!string.IsNullOrWhiteSpace(text))
                        {
                            values.Add(text!);
                        }
                        else
                        {
                            error = $"{key} contains an empty value";
                            return false;
                        }
                        continue;
                    }

                    error = $"{key} contains an unsupported value";
                    return false;
                }

                deletes = values.ToArray();
                return true;
            }
            catch (JsonException ex)
            {
                error = $"Invalid {key} JSON payload: {ex.Message}";
                return false;
            }
        }

        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return true;
        }

        var parsedValues = new object[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                parsedValues[i] = parsed;
            }
            else
            {
                parsedValues[i] = parts[i];
            }
        }

        deletes = parsedValues;
        return true;
    }

    private static bool TryParseFormBool(
        IFormCollection form,
        string key,
        out bool value,
        out string? error)
    {
        value = false;
        error = null;

        if (!form.TryGetValue(key, out var rawValue))
        {
            return true;
        }

        var raw = rawValue.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (bool.TryParse(raw, out var parsed))
        {
            value = parsed;
            return true;
        }

        if (raw is "1" or "0")
        {
            value = raw == "1";
            return true;
        }

        error = $"{key} must be a boolean.";
        return false;
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
            GetTimeoutAwareCancellationToken(context));

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
            GetTimeoutAwareCancellationToken(context));

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
            using var jsonDoc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: GetTimeoutAwareCancellationToken(context));
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
        catch (Exception)
        {
            return (false, null, "Failed to parse request");
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
    /// <param name="context">HTTP context for response headers and timeouts</param>
    /// <param name="featureStore">Feature store service</param>
    /// <param name="layerCatalog">Layer catalog service</param>
    /// <param name="tileOptions">Tile configuration options</param>
    /// <param name="logger">Logger for tile failures</param>
    /// <returns>MVT tile data or 204 if empty</returns>
    private static async Task<IResult> HandleGetTile(
        int layerId,
        int z,
        int x,
        int y,
        string? where,
        HttpContext context,
        IFeatureStore featureStore,
        ILayerCatalog layerCatalog,
        IOptions<TileOptions> tileOptions,
        ILogger<FeatureServerEndpointsLog> logger)
    {
        try
        {
            var cancellationToken = GetTimeoutAwareCancellationToken(context);

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
            context.Response.Headers["Cache-Control"] = $"public, max-age={options.CacheMaxAge}";
            return Results.Bytes(mvtData, "application/vnd.mapbox-vector-tile");
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            Log.TileGenerationFailed(logger, layerId, z, x, y, ex);
            return Results.Problem(
                detail: "An error occurred while generating the tile.",
                statusCode: 500,
                title: "Tile generation failed");
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 3200, Level = LogLevel.Error, Message = "Tile generation failed for layer {LayerId} at {Z}/{X}/{Y}")]
        public static partial void TileGenerationFailed(ILogger logger, int layerId, int z, int x, int y, Exception exception);
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
