// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.FeatureServer.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.FeatureServer;

/// <summary>
/// Extension methods to register FeatureServer endpoints
/// </summary>
public static class FeatureServerEndpoints
{
    /// <summary>
    /// Maps FeatureServer REST API endpoints for layer metadata
    /// </summary>
    [RequiresUnreferencedCode()]
    [RequiresDynamicCode()]
    public static IEndpointRouteBuilder MapFeatureServerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/rest/services")
            .WithTags("FeatureServer");

        // Service metadata endpoint
        group.MapGet("/{serviceId}/FeatureServer", GetServiceMetadataAsync)
            .WithName("GetServiceMetadata")
            .WithSummary("Get FeatureServer service metadata")
            .WithDescription("Returns metadata for a FeatureServer service including all layers")
            .Produces<FeatureServerResponse>(200, "application/json")
            .Produces(404);

        // Layer metadata endpoint
        group.MapGet("/{serviceId}/FeatureServer/{layerId:int}", GetLayerMetadataAsync)
            .WithName("GetLayerMetadata")
            .WithSummary("Get FeatureServer layer metadata")
            .WithDescription("Returns detailed metadata for a specific layer")
            .Produces<LayerResponse>(200, "application/json")
            .Produces(404);

        return endpoints;
    }

    /// <summary>
    /// Handles service metadata requests
    /// </summary>
    internal static async Task<IResult> GetServiceMetadataAsync(
        [FromRoute] string serviceId,
        [FromServices] ILayerCatalog catalog,
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

            var response = MapServiceToResponse(service);

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

            var response = MapLayerToResponse(layer);

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
    private static FeatureServerResponse MapServiceToResponse(ServiceDefinition service)
    {
        return new FeatureServerResponse
        {
            ServiceName = service.Name,
            ServiceDescription = service.Description,
            Layers = service.Layers.Select(MapLayerInfo).ToArray(),
            SpatialReference = MapSpatialReference(service.SpatialReference),
            InitialExtent = service.EffectiveExtent.HasValue ? MapExtent(service.EffectiveExtent.Value) : null,
            FullExtent = service.EffectiveExtent.HasValue ? MapExtent(service.EffectiveExtent.Value) : null,
            MaxRecordCount = service.MaxRecordCount,
            SupportedQueryFormats = service.SupportedFormats,
            Capabilities = string.Join(",", service.Capabilities),
            Fields = service.AllFields.Select(MapFieldInfo).ToArray()
        };
    }

    /// <summary>
    /// Maps LayerDefinition to LayerResponse
    /// </summary>
    private static LayerResponse MapLayerToResponse(LayerDefinition layer)
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
}
