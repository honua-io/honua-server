// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.MapServer.Models;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.MapServer;

internal static partial class MapServerEndpoints
{
    /// <summary>
    /// Handle MapServer service metadata requests.
    /// </summary>
    private static async Task<IResult> HandleGetServiceMetadata(HttpContext context)
    {
        if (!TryValidateMetadataFormat(context.Request.Query, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, formatError ?? "Output format is not supported.");
        }

        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, context.RequestAborted);
        if (!serviceResult.IsValid)
        {
            var errorMessage = serviceResult.ErrorMessage ?? "Service not found.";
            if (serviceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
            }

            return StandardErrorHelpers.CreateNotFound(context, errorMessage);
        }

        var service = serviceResult.Resource!;
        var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(context, service, ServiceProtocols.MapServer);
        if (protocolError is not null)
        {
            return protocolError;
        }

        var accessError = AccessPolicyHelpers.RequireAnyLayerAccess(context, service.Layers, service);
        if (accessError != null)
        {
            return accessError;
        }

        var visibleLayers = service.Layers
            .Where(layer => AccessPolicyHelpers.IsLayerAccessible(context, layer, service))
            .ToArray();

        var response = MapServiceToMapServerResponse(service with { Layers = visibleLayers });
        return Results.Json(response, MapServerJsonContext.Default.MapServerResponse, contentType: "application/json");
    }

    /// <summary>
    /// Handle MapServer layer metadata requests.
    /// </summary>
    private static async Task<IResult> HandleGetLayerMetadata(HttpContext context)
    {
        if (!TryValidateMetadataFormat(context.Request.Query, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, formatError ?? "Output format is not supported.");
        }

        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var layerError = RouteValidationHelpers.ValidateLayerId(context, out var layerId);
        if (layerError is not null)
        {
            return layerError;
        }

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var serviceLayerResult = await resourceValidator.ValidateServiceLayerAsync(serviceId, layerId, context.RequestAborted);
        if (!serviceLayerResult.IsValid)
        {
            var errorMessage = serviceLayerResult.ErrorMessage ?? "Resource not found.";
            if (serviceLayerResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
            }

            return StandardErrorHelpers.CreateNotFound(context, errorMessage);
        }

        var service = serviceLayerResult.Resource!.Service;
        var layer = serviceLayerResult.Resource!.Layer;

        var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(context, service, ServiceProtocols.MapServer);
        if (protocolError is not null)
        {
            return protocolError;
        }

        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
        if (accessError != null)
        {
            return accessError;
        }

        var limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value;
        var response = MapLayerToMapServerLayerResponse(service, layer, limitsOptions.Query.MaxRecordCount);
        return Results.Json(response, MapServerJsonContext.Default.MapServerLayerResponse, contentType: "application/json");
    }

    private static bool TryValidateMetadataFormat(IQueryCollection query, out string? error)
    {
        error = null;
        if (!query.TryGetValue("f", out var formatValues))
        {
            return true;
        }

        var format = formatValues.ToString();
        if (string.IsNullOrWhiteSpace(format))
        {
            return true;
        }

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(format, "pjson", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        error = $"Output format '{format}' is not supported.";
        return false;
    }

    private static MapServerResponse MapServiceToMapServerResponse(ServiceDefinition service)
    {
        var mapConfig = service.Metadata?.MapServer;
        var visibleFeatureLayers = service.Layers.Where(layer => layer.HasGeometry).ToArray();
        var visibleTables = service.Layers.Where(layer => !layer.HasGeometry).ToArray();

        return new MapServerResponse
        {
            CurrentVersion = 10.81,
            ServiceDescription = service.Description,
            MapName = service.Name,
            Description = service.Description,
            SpatialReference = EsriSpatialReference.FromSpatialReference(service.SpatialReference),
            Layers = [.. visibleFeatureLayers.Select(layer => new MapServerLayerInfo
            {
                Id = layer.Id,
                Name = layer.Name,
                DefaultVisibility = layer.DefaultVisibility,
                MinScale = layer.MinScale,
                MaxScale = layer.MaxScale
            })],
            Tables = [.. visibleTables.Select(layer => new MapServerTableInfo
            {
                Id = layer.Id,
                Name = layer.Name
            })],
            SupportedImageFormatTypes = "PNG,PNG8,PNG24,PNG32,JPG,GIF",
            SupportsDynamicLayers = true,
            SingleFusedMapCache = false,
            Units = ResolveMapUnits(service.SpatialReference),
            Capabilities = BuildMapServerCapabilities(service),
            FullExtent = service.EffectiveExtent.HasValue
                ? EsriExtent.FromFeatureExtent(service.EffectiveExtent.Value)
                : null,
            InitialExtent = service.EffectiveExtent.HasValue
                ? EsriExtent.FromFeatureExtent(service.EffectiveExtent.Value)
                : null,
            MaxImageWidth = mapConfig?.MaxImageWidth ?? 4096,
            MaxImageHeight = mapConfig?.MaxImageHeight ?? 4096
        };
    }

    private static MapServerLayerResponse MapLayerToMapServerLayerResponse(
        ServiceDefinition service,
        LayerDefinition layer,
        int maxRecordCount)
    {
        var objectIdField = layer.PrimaryKeyField?.Name ?? FieldNames.ObjectId;
        var displayField = ResolveDisplayField(layer, objectIdField);
        var layerCapabilities = BuildMapServerLayerCapabilities(service, layer);

        return new MapServerLayerResponse
        {
            CurrentVersion = 10.81,
            Id = layer.Id,
            Name = layer.Name,
            Type = layer.HasGeometry ? "Feature Layer" : "Table",
            Description = layer.Description,
            GeometryType = layer.HasGeometry ? MapGeometryTypeToEsri(layer.GeometryType) : null,
            SpatialReference = EsriSpatialReference.FromSpatialReference(layer.SpatialReference),
            Extent = layer.Extent.HasValue ? EsriExtent.FromFeatureExtent(layer.Extent.Value) : null,
            DisplayField = displayField,
            ObjectIdField = objectIdField,
            Fields = [.. layer.Fields.Select(field => new MapServerFieldInfo
            {
                Name = field.Name,
                Type = field.GeoServicesType,
                Alias = field.DisplayName,
                Length = field.Length,
                Nullable = field.Nullable,
                Editable = !field.IsGeometry,
                DefaultValue = field.DefaultValue
            })],
            Capabilities = layerCapabilities,
            SupportsAdvancedQueries = service.SupportsAdvancedQueries,
            HasAttachments = layer.SupportsAttachments,
            MinScale = layer.MinScale,
            MaxScale = layer.MaxScale,
            DefaultVisibility = layer.DefaultVisibility,
            MaxRecordCount = maxRecordCount
        };
    }

    private static string ResolveMapUnits(Honua.Core.Features.Shared.Models.SpatialReference spatialReference)
        => spatialReference.IsGeographic ? "esriDecimalDegrees" : "esriMeters";

    private static string ResolveDisplayField(LayerDefinition layer, string objectIdField)
    {
        var stringField = layer.AttributeFields
            .FirstOrDefault(field => field.Type == FieldType.String);
        return stringField?.Name ?? objectIdField;
    }

    private static string BuildMapServerCapabilities(ServiceDefinition service)
    {
        var capabilities = new List<string> { "Map" };

        if (service.Capabilities.Any(cap => cap.Equals("Query", StringComparison.OrdinalIgnoreCase)))
        {
            capabilities.Add("Query");
            capabilities.Add("Data");
        }

        if (service.SupportsEditing)
        {
            capabilities.Add("Create");
            capabilities.Add("Update");
            capabilities.Add("Delete");
            capabilities.Add("Editing");
        }

        if (service.Capabilities.Any(cap => cap.Equals("Extract", StringComparison.OrdinalIgnoreCase)))
        {
            capabilities.Add("Extract");
        }

        return string.Join(',', capabilities.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildMapServerLayerCapabilities(ServiceDefinition service, LayerDefinition layer)
    {
        var capabilities = new List<string>();
        if (layer.HasGeometry)
        {
            capabilities.Add("Map");
        }

        if (service.Capabilities.Any(cap => cap.Equals("Query", StringComparison.OrdinalIgnoreCase)))
        {
            capabilities.Add("Query");
            capabilities.Add("Data");
        }

        if (service.SupportsEditing)
        {
            capabilities.Add("Create");
            capabilities.Add("Update");
            capabilities.Add("Delete");
            capabilities.Add("Editing");
        }

        return string.Join(',', capabilities.Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
