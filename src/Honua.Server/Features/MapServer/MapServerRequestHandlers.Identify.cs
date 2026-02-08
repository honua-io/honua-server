// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.MapServer.Models;
using Honua.Server.Features.MapServer.Rendering;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.MapServer;

internal static partial class MapServerEndpoints
{
    private const int DefaultTolerance = 3;
    private const int MaxIdentifyResults = 100;

    /// <summary>
    /// Handle MapServer identify (click-query) requests.
    /// </summary>
    private static async Task<IResult> HandleIdentify(HttpContext context)
    {
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.MapServerEndpoints");

        var query = context.Request.Query;
        var parameters = new IdentifyParameters
        {
            Geometry = query["geometry"].FirstOrDefault(),
            GeometryType = query["geometryType"].FirstOrDefault() ?? "esriGeometryPoint",
            Sr = query["sr"].FirstOrDefault(),
            Layers = query["layers"].FirstOrDefault(),
            Tolerance = int.TryParse(query["tolerance"].FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tol)
                ? tol
                : DefaultTolerance,
            MapExtent = query["mapExtent"].FirstOrDefault(),
            ImageDisplay = query["imageDisplay"].FirstOrDefault(),
            ReturnGeometry = !string.Equals(query["returnGeometry"].FirstOrDefault(), "false", StringComparison.OrdinalIgnoreCase),
            F = query["f"].FirstOrDefault() ?? "json"
        };

        // Parse point geometry
        if (!TryParseIdentifyPoint(parameters.Geometry, out var pointX, out var pointY))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Invalid or missing geometry parameter. Expected format: x,y");
        }

        MapServerLog.IdentifyRequested(logger, serviceId,
            pointX.ToString(CultureInfo.InvariantCulture),
            pointY.ToString(CultureInfo.InvariantCulture));

        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.MapServerIdentify, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.MapServer);
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "identify");

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, context.RequestAborted);
        if (!serviceResult.IsValid)
        {
            var errorMessage = serviceResult.ErrorMessage ?? "Service not found.";
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

        // Determine layers to identify
        var identifyLayers = ResolveIdentifyLayers(service, parameters.Layers, context);

        // Calculate search tolerance in map units
        var searchTolerance = CalculateSearchTolerance(
            parameters.Tolerance,
            parameters.MapExtent,
            parameters.ImageDisplay);

        var geometrySrid = TryParseSrid(parameters.Sr) ?? service.SpatialReference.Srid;
        var serviceSrid = service.SpatialReference.Srid;

        // Transform point to service SRID if needed
        var (queryX, queryY) = CoordinateTransformer.TransformPoint(pointX, pointY, geometrySrid, serviceSrid);

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var results = new List<IdentifyResult>();

        foreach (var layer in identifyLayers)
        {
            if (!layer.HasGeometry)
            {
                continue;
            }

            // Create a point buffer for spatial query
            var bufferWkb = CreatePointBufferWkb(queryX, queryY, searchTolerance);
            var spatialFilter = SpatialFilter.Create(bufferWkb, SpatialRelationship.Intersects, serviceSrid);

            var featureQuery = new FeatureQuery
            {
                SpatialFilter = spatialFilter,
                SpatialReferenceSrid = serviceSrid,
                OutputSrid = geometrySrid,
                Limit = MaxIdentifyResults
            };

            var queryResult = await featureReader.QueryAsync(layer.Id, featureQuery, context.RequestAborted);

            foreach (var feature in queryResult.Items)
            {
                var attributes = new Dictionary<string, object?>();
                foreach (var kvp in feature.Attributes)
                {
                    attributes[kvp.Key] = kvp.Value;
                }

                var displayValue = GetDisplayFieldValue(feature, layer);

                var identifyResult = new IdentifyResult
                {
                    LayerId = layer.Id,
                    LayerName = layer.Name,
                    Value = displayValue,
                    Attributes = attributes,
                    GeometryType = MapGeometryTypeToEsri(layer.GeometryType),
                    Geometry = parameters.ReturnGeometry && feature.Geometry != null
                        ? ConvertGeometryToJson(feature.Geometry, layer.GeometryType)
                        : null
                };

                results.Add(identifyResult);
            }
        }

        MapServerLog.IdentifyCompleted(logger, serviceId, results.Count);
        HonuaTelemetry.SetSuccess(activity, results.Count);

        var response = new IdentifyResponse { Results = [.. results] };
        return Results.Json(response, MapServerJsonContext.Default.IdentifyResponse, contentType: "application/json");
    }

    private static bool TryParseIdentifyPoint(string? geometry, out double x, out double y)
    {
        x = 0;
        y = 0;

        if (string.IsNullOrWhiteSpace(geometry))
        {
            return false;
        }

        // Try "x,y" format
        var parts = geometry.Split(',');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
        {
            return true;
        }

        // Try JSON point format: {"x":..., "y":...}
        if (TryParseIdentifyPointJson(geometry, out x, out y))
        {
            return true;
        }

        return false;
    }

    private static bool TryParseIdentifyPointJson(string geometry, out double x, out double y)
    {
        x = 0;
        y = 0;

        try
        {
            var utf8 = Encoding.UTF8.GetBytes(geometry);
            var reader = new Utf8JsonReader(utf8, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            bool foundX = false;
            bool foundY = false;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                var name = reader.GetString();
                if (!reader.Read())
                {
                    break;
                }

                if (string.Equals(name, "x", StringComparison.OrdinalIgnoreCase))
                {
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var parsedX))
                    {
                        x = parsedX;
                        foundX = true;
                    }
                }
                else if (string.Equals(name, "y", StringComparison.OrdinalIgnoreCase))
                {
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var parsedY))
                    {
                        y = parsedY;
                        foundY = true;
                    }
                }
            }

            return foundX && foundY;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static LayerDefinition[] ResolveIdentifyLayers(
        ServiceDefinition service,
        string? layersParam,
        HttpContext context)
    {
        var accessibleLayers = service.Layers
            .Where(l => AccessPolicyHelpers.IsLayerAccessible(context, l, service))
            .ToArray();

        if (string.IsNullOrWhiteSpace(layersParam))
        {
            return accessibleLayers.Where(l => l.DefaultVisibility).ToArray();
        }

        var spec = layersParam.Trim();

        // Parse "all:0,1,2" or "visible:0,1,2" or "top:0,1" or just "0,1,2"
        string idPart;
        if (spec.Contains(':'))
        {
            var colonIndex = spec.IndexOf(':');
            idPart = spec[(colonIndex + 1)..];
        }
        else
        {
            idPart = spec;
        }

        var ids = ParseLayerIds(idPart);
        return accessibleLayers.Where(l => ids.Contains(l.Id)).ToArray();
    }

    private static double CalculateSearchTolerance(
        int pixelTolerance,
        string? mapExtentStr,
        string? imageDisplayStr)
    {
        if (string.IsNullOrWhiteSpace(mapExtentStr) || string.IsNullOrWhiteSpace(imageDisplayStr))
        {
            // Fallback: assume small geographic tolerance
            return 0.001;
        }

        if (!TryParseBbox(mapExtentStr, out var mapExtent))
        {
            return 0.001;
        }

        // Parse imageDisplay: "width,height,dpi"
        var displayParts = imageDisplayStr.Split(',');
        if (displayParts.Length >= 1 &&
            int.TryParse(displayParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var imageWidth) &&
            imageWidth > 0)
        {
            return CoordinateTransformer.PixelToMapUnits(pixelTolerance, mapExtent, imageWidth);
        }

        return 0.001;
    }

    /// <summary>
    /// Creates a WKB polygon representing a square buffer around a point.
    /// </summary>
    private static byte[] CreatePointBufferWkb(double x, double y, double tolerance)
    {
        return CreateEnvelopeWkb(
            x - tolerance,
            y - tolerance,
            x + tolerance,
            y + tolerance);
    }

    private static string? GetDisplayFieldValue(Feature feature, LayerDefinition layer)
    {
        // Try first string attribute as display value
        foreach (var kvp in feature.Attributes)
        {
            if (kvp.Value is string s && !string.IsNullOrWhiteSpace(s))
            {
                return s;
            }
        }

        return feature.Id.ToString(CultureInfo.InvariantCulture);
    }

    private static string? MapGeometryTypeToEsri(GeometryType geometryType)
    {
        return geometryType switch
        {
            GeometryType.Point or
            GeometryType.MultiPoint => "esriGeometryPoint",
            GeometryType.LineString or
            GeometryType.MultiLineString => "esriGeometryPolyline",
            GeometryType.Polygon or
            GeometryType.MultiPolygon => "esriGeometryPolygon",
            _ => null
        };
    }

    /// <summary>
    /// Converts WKB geometry to a simple JSON-serializable object for identify results.
    /// Returns coordinate arrays for the geometry.
    /// </summary>
    private static object? ConvertGeometryToJson(byte[] wkb, GeometryType geometryType)
    {
        // For identify, return a simplified coordinate representation
        // Parse WKB to extract coordinates
        try
        {
            var coords = ExtractCoordinatesFromWkb(wkb);
            if (coords == null)
            {
                return null;
            }

            return geometryType switch
            {
                GeometryType.Point or
                GeometryType.MultiPoint => new { x = coords[0].X, y = coords[0].Y },
                GeometryType.LineString or
                GeometryType.MultiLineString => new
                {
                    paths = new[] { coords.Select(c => new[] { c.X, c.Y }).ToArray() }
                },
                GeometryType.Polygon or
                GeometryType.MultiPolygon => new
                {
                    rings = new[] { coords.Select(c => new[] { c.X, c.Y }).ToArray() }
                },
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static (double X, double Y)[]? ExtractCoordinatesFromWkb(byte[] wkb)
    {
        if (wkb.Length < 5)
        {
            return null;
        }

        var isLittleEndian = wkb[0] == 1;
        var typeValue = isLittleEndian
            ? BitConverter.ToInt32(wkb, 1)
            : BinaryPrimitives_ReadInt32BigEndian(wkb, 1);

        // Strip SRID flag and Z/M flags
        var baseType = typeValue & 0xFF;
        var hasSrid = (typeValue & 0x20000000) != 0;
        var hasZ = (typeValue & 0x80000000u) != 0 || (baseType > 1000 && baseType < 2000);
        var hasM = (typeValue & 0x40000000) != 0 || (baseType > 2000 && baseType < 3000);

        if (baseType > 1000)
        {
            baseType %= 1000;
        }

        var offset = 5;
        if (hasSrid)
        {
            offset += 4;
        }

        var coordSize = 2 + (hasZ ? 1 : 0) + (hasM ? 1 : 0);

        return baseType switch
        {
            1 => ReadPoint(wkb, offset, isLittleEndian, coordSize),
            2 => ReadLineString(wkb, ref offset, isLittleEndian, coordSize),
            3 => ReadPolygonCoords(wkb, ref offset, isLittleEndian, coordSize),
            _ => null
        };
    }

    private static (double X, double Y)[]? ReadPoint(byte[] wkb, int offset, bool isLittleEndian, int coordSize)
    {
        if (wkb.Length < offset + 16)
        {
            return null;
        }

        var x = ReadDouble(wkb, offset, isLittleEndian);
        var y = ReadDouble(wkb, offset + 8, isLittleEndian);
        return [(x, y)];
    }

    private static (double X, double Y)[]? ReadLineString(byte[] wkb, ref int offset, bool isLittleEndian, int coordSize)
    {
        if (wkb.Length < offset + 4)
        {
            return null;
        }

        var numPoints = ReadInt32(wkb, offset, isLittleEndian);
        offset += 4;

        var coords = new (double X, double Y)[numPoints];
        for (var i = 0; i < numPoints; i++)
        {
            coords[i] = (ReadDouble(wkb, offset, isLittleEndian), ReadDouble(wkb, offset + 8, isLittleEndian));
            offset += coordSize * 8;
        }

        return coords;
    }

    private static (double X, double Y)[]? ReadPolygonCoords(byte[] wkb, ref int offset, bool isLittleEndian, int coordSize)
    {
        if (wkb.Length < offset + 4)
        {
            return null;
        }

        var numRings = ReadInt32(wkb, offset, isLittleEndian);
        offset += 4;

        if (numRings == 0)
        {
            return null;
        }

        // Just read the first ring
        return ReadLineString(wkb, ref offset, isLittleEndian, coordSize);
    }

    private static double ReadDouble(byte[] buffer, int offset, bool isLittleEndian)
    {
        if (isLittleEndian == BitConverter.IsLittleEndian)
        {
            return BitConverter.ToDouble(buffer, offset);
        }

        Span<byte> temp = stackalloc byte[8];
        buffer.AsSpan(offset, 8).CopyTo(temp);
        temp.Reverse();
        return BitConverter.ToDouble(temp);
    }

    private static int ReadInt32(byte[] buffer, int offset, bool isLittleEndian)
    {
        if (isLittleEndian == BitConverter.IsLittleEndian)
        {
            return BitConverter.ToInt32(buffer, offset);
        }

        Span<byte> temp = stackalloc byte[4];
        buffer.AsSpan(offset, 4).CopyTo(temp);
        temp.Reverse();
        return BitConverter.ToInt32(temp);
    }

    private static int BinaryPrimitives_ReadInt32BigEndian(byte[] buffer, int offset)
    {
        return (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
    }
}
