// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Protocols.GeoServices.MapServer.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Protocols.GeoServices.MapServer;

internal static partial class MapServerEndpoints
{
    private const string DefaultMapServerCapabilities = "Map,Query,Data,Extract";

    private sealed record MapServerMetadataLayerDescriptor(
        int PublicLayerId,
        int StorageLayerId,
        string Name,
        MetadataV2Publication Publication,
        MetadataV2Resource Resource);

    /// <summary>
    /// Handle MapServer service metadata requests.
    /// </summary>
    private static async Task<IResult> HandleGetServiceMetadata(HttpContext context)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        if (!TryValidateMetadataFormat(context.Request.Query, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, formatError ?? "Output format is not supported.");
        }

        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            "metadata",
            HonuaTelemetry.Protocols.MapServer,
            "*");
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId)
            .WithTag(HonuaTelemetry.Tags.Operation, "get-service-metadata");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
            var serviceResult = await ServiceResourceValidationHelpers.ValidateServiceV2Async(
                resourceValidator,
                serviceId,
                ServiceProtocols.MapServer,
                context,
                cancellationToken: cancellationToken);
            if (!serviceResult.IsValid)
            {
                return serviceResult.ErrorResult!;
            }

            var service = serviceResult.Service!;
            var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var publishedLayers = ResolveMapServerMetadataLayers(snapshot, service);

            var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(
                context,
                publishedLayers.Select(static layer => layer.Resource),
                service);
            if (accessError != null)
            {
                return accessError;
            }

            var visibleLayers = publishedLayers
                .Where(layer => AccessPolicyHelpers.IsResourceAccessible(context, layer.Resource, service))
                .ToArray();

            var limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value;
            var response = MapServiceToMapServerResponse(
                service,
                visibleLayers,
                limitsOptions.Query.MaxRecordCount,
                limitsOptions.Tiles.MaxTileZoom);

            stopwatch.Stop();
            scope.SetSuccess(visibleLayers.Length);
            scope.CategorizeLatency(stopwatch.Elapsed.TotalMilliseconds);

            return Results.Json(response, MapServerJsonContext.Default.MapServerResponse, contentType: "application/json");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            scope.RecordException(ex);
            throw;
        }
    }

    /// <summary>
    /// Handle MapServer layer metadata requests.
    /// </summary>
    private static async Task<IResult> HandleGetLayerMetadata(HttpContext context)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
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

        using var scope = HonuaTelemetryScope.StartFeature(
            "metadata",
            HonuaTelemetry.Protocols.MapServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId)
            .WithTag(HonuaTelemetry.Tags.Operation, "get-layer-metadata");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
            var serviceLayerResult = await ServiceResourceValidationHelpers.ValidateServiceLayerV2Async(
                resourceValidator,
                serviceId,
                layerId,
                ServiceProtocols.MapServer,
                context,
                cancellationToken: cancellationToken);
            if (!serviceLayerResult.IsValid)
            {
                return serviceLayerResult.ErrorResult!;
            }

            var service = serviceLayerResult.Service!;
            var publication = serviceLayerResult.Publication!;
            var resource = serviceLayerResult.Resource!;

            var accessError = AccessPolicyHelpers.RequireResourceAccess(context, resource, service);
            if (accessError != null)
            {
                return accessError;
            }

            var limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value;

            var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var drawingInfo = ResolveMapServerDrawingInfo(resource, snapshot);

            var response = MapLayerToMapServerLayerResponse(
                service,
                publication,
                resource,
                snapshot,
                limitsOptions.Query.MaxRecordCount,
                drawingInfo);

            stopwatch.Stop();
            scope.SetSuccess(1);
            scope.CategorizeLatency(stopwatch.Elapsed.TotalMilliseconds);

            return Results.Json(response, MapServerJsonContext.Default.MapServerLayerResponse, contentType: "application/json");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            scope.RecordException(ex);
            throw;
        }
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

    private static MapServerResponse MapServiceToMapServerResponse(
        MetadataV2Service service,
        IReadOnlyList<MapServerMetadataLayerDescriptor> layers,
        int maxRecordCount,
        int maxTileZoom)
    {
        var serviceSpatialReference = ResolveServiceSpatialReference(service, layers);
        var serviceExtent = ResolveServiceExtent(layers, serviceSpatialReference);
        var visibleFeatureLayers = layers.Where(static layer => HasMapServerGeometry(layer.Resource)).ToArray();
        var visibleTables = layers.Where(static layer => !HasMapServerGeometry(layer.Resource)).ToArray();

        return new MapServerResponse
        {
            CurrentVersion = 10.81,
            ServiceDescription = service.Metadata.Description,
            MapName = service.Metadata.Name,
            Description = service.Metadata.Description,
            SpatialReference = ToEsriSpatialReference(serviceSpatialReference),
            Layers = [.. visibleFeatureLayers.Select(layer => new MapServerLayerInfo
            {
                Id = layer.PublicLayerId,
                Name = layer.Name,
                DefaultVisibility = layer.Resource.Display?.DefaultVisibility ?? true,
                MinScale = layer.Resource.Display?.MinScale ?? 0,
                MaxScale = layer.Resource.Display?.MaxScale ?? 0
            })],
            Tables = [.. visibleTables.Select(layer => new MapServerTableInfo
            {
                Id = layer.PublicLayerId,
                Name = layer.Name
            })],
            CopyrightText = string.Empty,
            SupportedImageFormatTypes = "PNG,PNG8,PNG24,PNG32,JPG,GIF",
            // The current MapServer implementation only accepts a narrow dynamicLayers subset
            // for interoperability; do not advertise the full ArcGIS dynamic-layer contract.
            SupportsDynamicLayers = false,
            SingleFusedMapCache = false,
            Units = ResolveMapUnits(serviceSpatialReference),
            Capabilities = BuildMapServerCapabilities(),
            FullExtent = serviceExtent,
            InitialExtent = serviceExtent,
            MaxImageWidth = service.Settings?.MaxImageWidth ?? 4096,
            MaxImageHeight = service.Settings?.MaxImageHeight ?? 4096,
            MaxRecordCount = maxRecordCount,
            SupportedQueryFormats = string.Join(",", NormalizeSupportedQueryFormats(service.Settings?.SupportedFormats)),
            MinScale = ResolveServiceMinScale(layers) ?? 0,
            MaxScale = ResolveServiceMaxScale(layers) ?? 0,
            DocumentInfo = new MapServerDocumentInfo
            {
                Title = service.Metadata.Name ?? "",
                Comments = service.Metadata.Description ?? "",
                Subject = service.Metadata.Description ?? ""
            },
            TileInfo = BuildTileInfo(maxTileZoom)
        };
    }

    private static TileInfo BuildTileInfo(int maxTileZoom)
    {
        const double webMercatorOrigin = SpatialConstants.WebMercatorExtent;
        const int tileSize = 256;
        const double pixelSize = 0.00028;

        var effectiveMaxZoom = Math.Clamp(maxTileZoom, 0, TileMath.MaxSupportedZoomLevel);
        var lods = new LevelOfDetail[effectiveMaxZoom + 1];
        for (var z = 0; z <= effectiveMaxZoom; z++)
        {
            var matrixSize = 1L << z;
            var resolution = 2.0 * webMercatorOrigin / (tileSize * (double)matrixSize);
            var scale = resolution / pixelSize;
            lods[z] = new LevelOfDetail
            {
                Level = z,
                Resolution = resolution,
                Scale = scale
            };
        }

        return new TileInfo
        {
            Rows = tileSize,
            Cols = tileSize,
            Dpi = 96,
            Format = "PNG",
            Origin = new TileOrigin
            {
                X = -webMercatorOrigin,
                Y = webMercatorOrigin
            },
            SpatialReference = new EsriSpatialReference
            {
                Wkid = 3857,
                LatestWkid = 3857
            },
            Lods = lods
        };
    }

    private static MapServerLayerResponse MapLayerToMapServerLayerResponse(
        MetadataV2Service service,
        MetadataV2Publication publication,
        MetadataV2Resource resource,
        MetadataV2GraphSnapshot snapshot,
        int maxRecordCount,
        JsonElement? drawingInfo = null)
    {
        var publicLayerId = publication.LayerIndex
            ?? snapshot.ResolveStorageLayerId(publication)
            ?? snapshot.ResolveStorageLayerId(resource)
            ?? -1;
        var layerName = string.IsNullOrWhiteSpace(resource.Metadata.Name)
            ? publication.Metadata.Name
            : resource.Metadata.Name;
        var objectIdField = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
        var displayField = ResolveDisplayField(resource, objectIdField);
        var serviceLayers = ResolveMapServerMetadataLayers(snapshot, service);
        var serviceExtent = ResolveServiceExtent(serviceLayers, ResolveServiceSpatialReference(service, serviceLayers));
        var layerCapabilities = BuildMapServerLayerCapabilities(resource);
        var hasGeometry = HasMapServerGeometry(resource);

        return new MapServerLayerResponse
        {
            CurrentVersion = 10.81,
            Id = publicLayerId,
            Name = string.IsNullOrWhiteSpace(layerName)
                ? publicLayerId.ToString(CultureInfo.InvariantCulture)
                : layerName,
            Type = hasGeometry ? "Feature Layer" : "Table",
            Description = resource.Metadata.Description,
            GeometryType = hasGeometry ? MapGeometryTypeToEsri(resource.ReadGeometryType()) : null,
            SpatialReference = ToEsriSpatialReference(ResolveLayerSpatialReference(resource)),
            Extent = ResolveLayerExtent(resource, serviceExtent),
            DisplayField = displayField,
            ObjectIdField = objectIdField,
            Fields = [.. resource.SchemaFields.Select(MapFieldInfoV2)],
            Capabilities = layerCapabilities,
            SupportsAdvancedQueries = true,
            HasAttachments = resource.Editing?.SupportsAttachments ?? false,
            MinScale = resource.Display?.MinScale ?? 0,
            MaxScale = resource.Display?.MaxScale ?? 0,
            DefaultVisibility = resource.Display?.DefaultVisibility ?? true,
            MaxRecordCount = maxRecordCount,
            DrawingInfo = drawingInfo.HasValue ? (object)drawingInfo.Value : null,
            SupportedQueryFormats = string.Join(",", NormalizeSupportedQueryFormats(service.Settings?.SupportedFormats)),
            SupportsOrderBy = true,
            SupportsDistinct = true,
            SupportsPagination = true,
            // The FeatureServer-backed query path supports outStatistics on the same
            // layer, so advertise the capability here too — previously this flag was
            // left false, and clients reading MapServer layer metadata incorrectly
            // concluded statistics were unavailable.
            SupportsStatistics = true
        };
    }

    private static MapServerMetadataLayerDescriptor[] ResolveMapServerMetadataLayers(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service)
    {
        var descriptors = new List<MapServerMetadataLayerDescriptor>();
        foreach (var publication in snapshot.Index.PublicationsByService[service.Metadata.Id])
        {
            if (publication.LayerIndex is not int publicLayerId)
            {
                continue;
            }

            var resource = snapshot.ResolveResource(publication);
            if (resource is null)
            {
                continue;
            }

            var storageLayerId = snapshot.ResolveStorageLayerId(publication)
                ?? snapshot.ResolveStorageLayerId(resource)
                ?? publicLayerId;
            var layerName = string.IsNullOrWhiteSpace(resource.Metadata.Name)
                ? publication.Metadata.Name
                : resource.Metadata.Name;

            descriptors.Add(new MapServerMetadataLayerDescriptor(
                publicLayerId,
                storageLayerId,
                string.IsNullOrWhiteSpace(layerName)
                    ? publicLayerId.ToString(CultureInfo.InvariantCulture)
                    : layerName,
                publication,
                resource));
        }

        return [.. descriptors.OrderBy(static layer => layer.PublicLayerId)];
    }

    private static string[] NormalizeSupportedQueryFormats(IReadOnlyList<string>? formats)
    {
        if (formats == null || formats.Count == 0)
        {
            return ["JSON"];
        }

        return [.. formats.Select(static format => format.ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static EsriExtent? ResolveLayerExtent(MetadataV2Resource resource, EsriExtent? serviceExtent)
    {
        var layerSpatialReference = ResolveLayerSpatialReference(resource);
        var bbox = resource.ReadBbox();
        if (bbox is not null)
        {
            return ToEsriExtent(bbox, layerSpatialReference);
        }

        if (HasMapServerGeometry(resource))
        {
            return serviceExtent;
        }

        return null;
    }

    private static EsriExtent? ResolveServiceExtent(
        IReadOnlyList<MapServerMetadataLayerDescriptor> layers,
        MetadataV2SpatialReference spatialReference)
    {
        double? west = null;
        double? south = null;
        double? east = null;
        double? north = null;

        foreach (var layer in layers)
        {
            var bbox = layer.Resource.ReadBbox();
            if (bbox is null)
            {
                continue;
            }

            west = west.HasValue ? Math.Min(west.Value, bbox.West) : bbox.West;
            south = south.HasValue ? Math.Min(south.Value, bbox.South) : bbox.South;
            east = east.HasValue ? Math.Max(east.Value, bbox.East) : bbox.East;
            north = north.HasValue ? Math.Max(north.Value, bbox.North) : bbox.North;
        }

        return west.HasValue && south.HasValue && east.HasValue && north.HasValue
            ? new EsriExtent
            {
                Xmin = west.Value,
                Ymin = south.Value,
                Xmax = east.Value,
                Ymax = north.Value,
                SpatialReference = ToEsriSpatialReference(spatialReference)
            }
            : null;
    }

    private static EsriExtent ToEsriExtent(
        MetadataV2Bbox bbox,
        MetadataV2SpatialReference spatialReference)
        => new()
        {
            Xmin = bbox.West,
            Ymin = bbox.South,
            Xmax = bbox.East,
            Ymax = bbox.North,
            SpatialReference = ToEsriSpatialReference(spatialReference)
        };

    private static double? ResolveServiceMinScale(IReadOnlyList<MapServerMetadataLayerDescriptor> layers)
    {
        var scales = layers
            .Select(static layer => layer.Resource.Display?.MinScale)
            .Where(static scale => scale is > 0)
            .Select(static scale => scale!.Value)
            .ToArray();
        return scales.Length > 0 ? scales.Max() : null;
    }

    private static double? ResolveServiceMaxScale(IReadOnlyList<MapServerMetadataLayerDescriptor> layers)
    {
        var scales = layers
            .Select(static layer => layer.Resource.Display?.MaxScale)
            .Where(static scale => scale is > 0)
            .Select(static scale => scale!.Value)
            .ToArray();
        return scales.Length > 0 ? scales.Min() : null;
    }

    private static MetadataV2SpatialReference ResolveServiceSpatialReference(
        MetadataV2Service service,
        IReadOnlyList<MapServerMetadataLayerDescriptor> layers)
        => service.SpatialReference
           ?? layers.Select(static layer => layer.Resource.Spatial?.SpatialReference)
               .FirstOrDefault(static spatialReference => spatialReference is not null)
           ?? MetadataV2SpatialReference.Wgs84;

    private static MetadataV2SpatialReference ResolveLayerSpatialReference(MetadataV2Resource resource)
        => resource.Spatial?.SpatialReference ?? MetadataV2SpatialReference.Wgs84;

    private static EsriSpatialReference ToEsriSpatialReference(MetadataV2SpatialReference spatialReference)
        => new()
        {
            Wkid = spatialReference.ResolveSrid() ?? SpatialReference.WGS84.Wkid
        };

    private static string ResolveMapUnits(MetadataV2SpatialReference spatialReference)
        => spatialReference.IsGeographic || spatialReference.ResolveSrid() == SpatialReference.WGS84.Wkid
            ? "esriDecimalDegrees"
            : "esriMeters";

    private static bool HasMapServerGeometry(MetadataV2Resource resource)
    {
        var geometryType = resource.ReadGeometryType();
        return geometryType != MetadataV2GeometryType.None ||
               resource.FindPrimaryGeometryField() is not null;
    }

    private static MapServerFieldInfo MapFieldInfoV2(MetadataV2Field field)
        => new()
        {
            Name = field.Name,
            Type = MapFieldTypeToGeoServicesV2(field.Type),
            Alias = field.Alias ?? field.Title ?? field.Name,
            Length = field.Length,
            Nullable = field.Nullable,
            Editable = field.Editable && field.Type is not MetadataV2FieldType.Geometry and not MetadataV2FieldType.Geography,
            DefaultValue = field.DefaultValue.HasValue ? field.DefaultValue.Value : null
        };

    private static string MapFieldTypeToGeoServicesV2(MetadataV2FieldType type)
        => type switch
        {
            MetadataV2FieldType.String => "esriFieldTypeString",
            MetadataV2FieldType.Integer => "esriFieldTypeInteger",
            MetadataV2FieldType.BigInteger => "esriFieldTypeInteger64",
            MetadataV2FieldType.Double => "esriFieldTypeDouble",
            MetadataV2FieldType.Float => "esriFieldTypeSingle",
            MetadataV2FieldType.Boolean => "esriFieldTypeSmallInteger",
            MetadataV2FieldType.DateTime => "esriFieldTypeDate",
            MetadataV2FieldType.Date => "esriFieldTypeDate",
            MetadataV2FieldType.Time => "esriFieldTypeString",
            MetadataV2FieldType.Json => "esriFieldTypeString",
            MetadataV2FieldType.Binary => "esriFieldTypeBlob",
            MetadataV2FieldType.Uuid => "esriFieldTypeGUID",
            MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography => "esriFieldTypeGeometry",
            _ => "esriFieldTypeString"
        };

    private static string BuildMapServerCapabilities()
        => DefaultMapServerCapabilities;

    private static string BuildMapServerLayerCapabilities(MetadataV2Resource resource)
    {
        var capabilities = new List<string>();
        if (HasMapServerGeometry(resource))
        {
            capabilities.Add("Map");
        }

        capabilities.Add("Query");
        capabilities.Add("Data");

        return string.Join(',', capabilities.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static JsonElement? ResolveMapServerDrawingInfo(
        MetadataV2Resource resource,
        MetadataV2GraphSnapshot snapshot)
    {
        if (TryResolveMapServerDrawingInfoExtension(resource, out var extensionDrawingInfo))
        {
            return extensionDrawingInfo;
        }

        foreach (var styleResourceId in resource.StyleResourceIds ?? Array.Empty<string>())
        {
            if (!snapshot.Index.ResourcesById.TryGetValue(styleResourceId, out var styleResource)
                || styleResource.Type != MetadataV2ResourceType.Style
                || styleResource.Style is null)
            {
                continue;
            }

            foreach (var encoding in styleResource.Style.Encodings ?? Array.Empty<MetadataV2StyleEncoding>())
            {
                if (!string.Equals(encoding.Encoding, "esri-drawing-info", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(encoding.Body))
                {
                    continue;
                }

                if (TryParseMapServerJsonElement(encoding.Body, out var drawingInfo))
                {
                    return drawingInfo;
                }
            }
        }

        return null;
    }

    private static bool TryResolveMapServerDrawingInfoExtension(
        MetadataV2Resource resource,
        out JsonElement drawingInfo)
    {
        if (resource.Extensions is not null
            && (resource.Extensions.TryGetValue("geoservices:drawingInfo", out drawingInfo)
                || resource.Extensions.TryGetValue("drawingInfo", out drawingInfo))
            && drawingInfo.ValueKind != JsonValueKind.Null
            && drawingInfo.ValueKind != JsonValueKind.Undefined)
        {
            return true;
        }

        drawingInfo = default;
        return false;
    }

    private static bool TryParseMapServerJsonElement(string json, out JsonElement element)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            element = document.RootElement.Clone();
            return element.ValueKind != JsonValueKind.Null && element.ValueKind != JsonValueKind.Undefined;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }
}
