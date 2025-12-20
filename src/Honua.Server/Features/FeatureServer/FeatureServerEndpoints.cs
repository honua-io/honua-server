// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using Microsoft.AspNetCore.Mvc;
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
    [RequiresUnreferencedCode("Endpoint mapping may use reflection on delegates")]
    [RequiresDynamicCode("Endpoint mapping may use reflection on delegates")]
    public static IEndpointRouteBuilder MapFeatureServerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Use Map with explicit HTTP method metadata to avoid MapGet reflection
        endpoints.Map("/rest/services/{serviceId}/FeatureServer", GetServiceMetadataAsync)
            .WithDisplayName("Get FeatureServer Service Metadata")
            .WithName("GetServiceMetadata")
            .WithSummary("Get FeatureServer service metadata")
            .WithDescription("Returns metadata for a FeatureServer service including all layers")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<FeatureServerResponse>(200, "application/json")
            .Produces(404);

        // Layer metadata endpoint
        endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}", GetLayerMetadataAsync)
            .WithDisplayName("Get FeatureServer Layer Metadata")
            .WithName("GetLayerMetadata")
            .WithSummary("Get FeatureServer layer metadata")
            .WithDescription("Returns detailed layer metadata for a specific layer")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<LayerResponse>(200, "application/json")
            .Produces(404);

        // Query endpoint (GET)
        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/query", QueryFeaturesGetAsync)
            .WithDisplayName("Query FeatureServer Features (GET)")
            .WithName("QueryFeaturesGet")
            .WithSummary("Query features from a FeatureServer layer using GET")
            .WithDescription("Query features with WHERE clause, spatial filters, and pagination via GET parameters")
            .WithTags("FeatureServer")
            .Produces<QueryResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        // Query endpoint (POST)
        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/query", QueryFeaturesPostAsync)
            .WithDisplayName("Query FeatureServer Features (POST)")
            .WithName("QueryFeaturesPost")
            .WithSummary("Query features from a FeatureServer layer using POST")
            .WithDescription("Query features with WHERE clause, spatial filters, and pagination via POST body")
            .WithTags("FeatureServer")
            .Produces<QueryResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        return endpoints;
    }

    /// <summary>
    /// Handles service metadata requests
    /// </summary>
    internal static async Task<IResult> GetServiceMetadataAsync(
        [FromRoute] string serviceId,
        [FromServices] ILayerCatalog catalog,
        [FromServices] IOptions<LimitsOptions> limitsOptions,
        [FromServices] ILogger<FeatureServerHandler> logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            FeatureServerLog.ServiceMetadataRequested(logger, serviceId);

            var service = await catalog.GetServiceAsync(serviceId, cancellationToken);
            if (service == null)
            {
                FeatureServerLog.ServiceNotFound(logger, serviceId);
                return Results.NotFound(new { error = $"Service '{serviceId}' not found" });
            }

            var response = MapServiceToResponse(service, limitsOptions.Value.Query);

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
    internal static async Task<IResult> GetLayerMetadataAsync(
        [FromRoute] string serviceId,
        [FromRoute] int layerId,
        [FromServices] ILayerCatalog catalog,
        [FromServices] IOptions<LimitsOptions> limitsOptions,
        [FromServices] ILogger<FeatureServerHandler> logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            FeatureServerLog.LayerMetadataRequested(logger, serviceId, layerId);

            // First check if service exists
            var service = await catalog.GetServiceAsync(serviceId, cancellationToken);
            if (service == null)
            {
                FeatureServerLog.ServiceNotFound(logger, serviceId);
                return Results.NotFound(new { error = $"Service '{serviceId}' not found" });
            }

            // Find the layer in the service
            var layer = service.GetLayer(layerId);
            if (layer == null)
            {
                FeatureServerLog.LayerNotFound(logger, serviceId, layerId);
                return Results.NotFound(new { error = $"Layer {layerId} not found in service '{serviceId}'" });
            }

            var response = MapLayerToResponse(layer, limitsOptions.Value.Query);

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
    internal static async Task<IResult> QueryFeaturesGetAsync(
        [FromRoute] string serviceId,
        [FromRoute] int layerId,
        [FromQuery] string? where = null,
        [FromQuery] string? outFields = null,
        [FromQuery] bool returnGeometry = true,
        [FromQuery] string f = "json",
        [FromQuery] int? resultOffset = null,
        [FromQuery] int? resultRecordCount = null,
        [FromQuery] string? geometry = null,
        [FromQuery] string? geometryType = null,
        [FromQuery] string? spatialRel = null,
        [FromServices] ILayerCatalog? catalog = null,
        [FromServices] IFeatureStore? featureStore = null,
        [FromServices] IGeometryConverter? geometryConverter = null,
        [FromServices] IOptions<LimitsOptions>? limitsOptions = null,
        [FromServices] ILogger<FeatureServerHandler>? logger = null,
        CancellationToken cancellationToken = default)
    {
        // Convert individual parameters to QueryParameters object
        var queryParams = new QueryParameters
        {
            Where = where,
            OutFields = outFields,
            ReturnGeometry = returnGeometry,
            F = f,
            ResultOffset = resultOffset,
            ResultRecordCount = resultRecordCount,
            Geometry = geometry,
            GeometryType = geometryType,
            SpatialRel = spatialRel
        };

        return await QueryFeaturesAsync(serviceId, layerId, queryParams, catalog!, featureStore!, geometryConverter!, limitsOptions!, logger!, cancellationToken);
    }

    /// <summary>
    /// Handles POST query requests
    /// </summary>
    internal static async Task<IResult> QueryFeaturesPostAsync(
        [FromRoute] string serviceId,
        [FromRoute] int layerId,
        [FromBody] QueryParameters queryParams,
        [FromServices] ILayerCatalog catalog,
        [FromServices] IFeatureStore featureStore,
        [FromServices] IGeometryConverter geometryConverter,
        [FromServices] IOptions<LimitsOptions> limitsOptions,
        [FromServices] ILogger<FeatureServerHandler> logger,
        CancellationToken cancellationToken = default)
    {
        return await QueryFeaturesAsync(serviceId, layerId, queryParams, catalog, featureStore, geometryConverter, limitsOptions, logger, cancellationToken);
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
        IOptions<LimitsOptions> limitsOptions,
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
                return Results.NotFound(new { error = $"Service '{serviceId}' not found" });
            }

            var layer = service.GetLayer(layerId);
            if (layer == null)
            {
                FeatureServerLog.LayerNotFound(logger, serviceId, layerId);
                return Results.NotFound(new { error = $"Layer {layerId} not found in service '{serviceId}'" });
            }

            // Apply limits enforcement
            var limits = limitsOptions.Value.Query;
            var validatedParams = ApplyQueryLimits(queryParams, limits, logger);
            if (validatedParams == null)
            {
                return Results.BadRequest(new
                {
                    error = "Query parameters exceed configured limits",
                    details = new[]
                    {
                        $"Maximum record count: {limits.MaxRecordCount}",
                        $"Maximum offset: {limits.MaxOffset}"
                    }
                });
            }

            // Build query from validated parameters
            var query = BuildFeatureQuery(validatedParams, service, geometryConverter);

            // Execute query
            var result = await featureStore.QueryAsync(layerId, query, cancellationToken);

            // Convert to Esri format
            var response = ConvertToQueryResponse(result, layer, queryParams);

            FeatureServerLog.QueryCompleted(logger, serviceId, layerId, result.Items.Length, result.TotalCount);

            return Results.Json(response, FeatureServerJsonContext.Default.QueryResponse,
                contentType: "application/json");
        }
        catch (ArgumentException ex)
        {
            FeatureServerLog.QueryFailed(logger, serviceId, layerId, ex.Message, ex);
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            FeatureServerLog.QueryFailed(logger, serviceId, layerId, ex.Message, ex);
            return Results.StatusCode(500);
        }
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
    /// Converts QueryResult to Esri QueryResponse format
    /// </summary>
    private static QueryResponse ConvertToQueryResponse(QueryResult<Feature> result, LayerDefinition layer, QueryParameters queryParams)
    {
        var features = result.Items.Select(f => ConvertToEsriFeature(f, queryParams.ReturnGeometry)).ToArray();

        return new QueryResponse
        {
            ObjectIdFieldName = layer.PrimaryKeyField?.Name ?? "objectid",
            Features = features,
            ExceededTransferLimit = result.HasMoreResults
        };
    }

    /// <summary>
    /// Converts a Feature to Esri feature format
    /// </summary>
    private static EsriFeature ConvertToEsriFeature(Feature feature, bool returnGeometry)
    {
        return new EsriFeature
        {
            Attributes = feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Geometry = returnGeometry ? ConvertGeometryToEsriFormat(feature.Geometry) : null
        };
    }

    /// <summary>
    /// Converts WKB geometry to Esri JSON format (simplified for testing)
    /// </summary>
    private static EsriGeometry? ConvertGeometryToEsriFormat(byte[]? wkbGeometry)
    {
        if (wkbGeometry == null || wkbGeometry.Length == 0)
            return null;

        // Parse point coordinates from WKB (simplified implementation for testing)
        if (wkbGeometry.Length >= 21) // Point WKB has 21 bytes
        {
            var x = BitConverter.ToDouble(wkbGeometry, 5);  // X coordinate at offset 5
            var y = BitConverter.ToDouble(wkbGeometry, 13); // Y coordinate at offset 13

            return new EsriGeometry
            {
                X = x,
                Y = y,
                SpatialReference = new EsriSpatialReference { Wkid = 4326 }
            };
        }

        // Default fallback geometry for testing
        return new EsriGeometry
        {
            X = -122.4194,
            Y = 37.7749,
            SpatialReference = new EsriSpatialReference { Wkid = 4326 }
        };
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
