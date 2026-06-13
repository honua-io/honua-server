// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.Infrastructure.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

internal sealed class ImageServerWmtsHandler(
    ImageServerTileHandler tileHandler,
    IMetadataV2GraphProvider graphProvider,
    IRasterStore rasterStore)
{
    private const int MaxZoom = 22;
    private const int TilePixels = 256;
    private const string ContentType = "application/xml";
    private const string TileMatrixSet = "WebMercatorQuad";
    private const string Version = "1.0.0";
    private const string PngFormat = "image/png";
    private const string JpegFormat = "image/jpeg";
    private const string JsonInfoFormat = "application/json";
    private const string XmlInfoFormat = "text/xml";
    private const double ScaleDenominator0 = 559082264.0287178;

    // Web Mercator (EPSG:3857) half-extent: the WebMercatorQuad origin shift.
    private const double OriginShift = 20037508.342789244;

    // Maps an advertised WMTS tile media type to the ImageServer tile-handler format token.
    private static bool TryResolveTileFormat(string mediaType, out string tileToken)
    {
        if (string.Equals(mediaType, PngFormat, StringComparison.OrdinalIgnoreCase))
        {
            tileToken = "png";
            return true;
        }

        if (string.Equals(mediaType, JpegFormat, StringComparison.OrdinalIgnoreCase))
        {
            tileToken = "jpg";
            return true;
        }

        tileToken = string.Empty;
        return false;
    }

    public async Task<IResult> HandleAsync(
        HttpContext context,
        int layerId,
        string advertisedLayerIdentifier,
        string? restPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(restPath))
        {
            return await HandleRestfulAsync(
                context,
                layerId,
                advertisedLayerIdentifier,
                restPath,
                cancellationToken).ConfigureAwait(false);
        }

        if (TryGetQueryValue(context.Request.Query, "SERVICE", out var service) &&
            !string.Equals(service, "WMTS", StringComparison.OrdinalIgnoreCase))
        {
            return CreateExceptionReport(
                "InvalidParameterValue",
                "service",
                "SERVICE must be WMTS.",
                StatusCodes.Status400BadRequest);
        }

        if (!TryGetQueryValue(context.Request.Query, "REQUEST", out var request) ||
            string.IsNullOrWhiteSpace(request) ||
            string.Equals(request, "GetCapabilities", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetQueryValue(context.Request.Query, "VERSION", out var capabilitiesVersion) &&
                !string.IsNullOrWhiteSpace(capabilitiesVersion) &&
                !string.Equals(capabilitiesVersion, Version, StringComparison.OrdinalIgnoreCase))
            {
                return CreateExceptionReport(
                    "VersionNegotiationFailed",
                    "version",
                    $"Only WMTS version {Version} is supported.",
                    StatusCodes.Status400BadRequest);
            }

            return await CreateCapabilitiesAsync(context, layerId, advertisedLayerIdentifier, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(request, "GetTile", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleGetTileAsync(
                context,
                layerId,
                advertisedLayerIdentifier,
                cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(request, "GetFeatureInfo", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleGetFeatureInfoAsync(
                context,
                layerId,
                advertisedLayerIdentifier,
                cancellationToken).ConfigureAwait(false);
        }

        return CreateExceptionReport(
            "OperationNotSupported",
            "request",
            "Only GetCapabilities, GetTile, and GetFeatureInfo are supported for ImageServer WMTS.",
            StatusCodes.Status501NotImplemented);
    }

    private async Task<IResult> HandleRestfulAsync(
        HttpContext context,
        int layerId,
        string advertisedLayerIdentifier,
        string restPath,
        CancellationToken cancellationToken)
    {
        var segments = restPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 2 &&
            TryDecodeSegment(segments[0], out var capabilitiesVersion) &&
            string.Equals(capabilitiesVersion, Version, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[1], "WMTSCapabilities.xml", StringComparison.OrdinalIgnoreCase))
        {
            return await CreateCapabilitiesAsync(context, layerId, advertisedLayerIdentifier, cancellationToken).ConfigureAwait(false);
        }

        if (segments.Length != 6 ||
            !TryDecodeSegment(segments[0], out var layerValue) ||
            !TryDecodeSegment(segments[1], out var styleValue) ||
            !TryDecodeSegment(segments[2], out var tileMatrixSetValue) ||
            !TryDecodeSegment(segments[3], out var tileMatrixValue) ||
            !TryDecodeSegment(segments[4], out var tileRowValue) ||
            !TryDecodeTileColumn(segments[5], out var tileColValue, out var formatValue))
        {
            return CreateExceptionReport(
                "InvalidParameterValue",
                "restPath",
                "WMTS RESTful path must be 1.0.0/WMTSCapabilities.xml or {Layer}/{Style}/{TileMatrixSet}/{TileMatrix}/{TileRow}/{TileCol}.png.",
                StatusCodes.Status400BadRequest);
        }

        return await HandleTileValuesAsync(
            context,
            layerId,
            advertisedLayerIdentifier,
            layerValue,
            styleValue,
            formatValue,
            tileMatrixSetValue,
            tileMatrixValue,
            tileRowValue,
            tileColValue,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IResult> HandleGetTileAsync(
        HttpContext context,
        int layerId,
        string advertisedLayerIdentifier,
        CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        if (!RequireQueryValue(query, "VERSION", out var version, out var error) ||
            !RequireQueryValue(query, "LAYER", out var layerValue, out error) ||
            !RequireQueryValue(query, "STYLE", out var styleValue, out error) ||
            !RequireQueryValue(query, "FORMAT", out var formatValue, out error) ||
            !RequireQueryValue(query, "TILEMATRIXSET", out var tileMatrixSet, out error) ||
            !RequireQueryValue(query, "TILEMATRIX", out var tileMatrix, out error) ||
            !RequireQueryValue(query, "TILEROW", out var tileRow, out error) ||
            !RequireQueryValue(query, "TILECOL", out var tileCol, out error))
        {
            return error!;
        }

        if (!string.Equals(version, Version, StringComparison.OrdinalIgnoreCase))
        {
            return CreateExceptionReport(
                "InvalidParameterValue",
                "version",
                $"VERSION must be {Version}.",
                StatusCodes.Status400BadRequest);
        }

        return await HandleTileValuesAsync(
            context,
            layerId,
            advertisedLayerIdentifier,
            layerValue,
            styleValue,
            formatValue,
            tileMatrixSet,
            tileMatrix,
            tileRow,
            tileCol,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IResult> HandleTileValuesAsync(
        HttpContext context,
        int layerId,
        string advertisedLayerIdentifier,
        string layerValue,
        string styleValue,
        string formatValue,
        string tileMatrixSet,
        string tileMatrix,
        string tileRow,
        string tileCol,
        CancellationToken cancellationToken)
    {
        if (!IsLayerIdentifierMatch(layerValue, layerId, advertisedLayerIdentifier))
        {
            return CreateExceptionReport(
                "InvalidParameterValue",
                "layer",
                "Invalid LAYER parameter.",
                StatusCodes.Status400BadRequest);
        }

        if (!string.Equals(styleValue, "default", StringComparison.OrdinalIgnoreCase))
        {
            return CreateExceptionReport(
                "InvalidParameterValue",
                "style",
                "Only STYLE=default is supported.",
                StatusCodes.Status400BadRequest);
        }

        if (!TryResolveTileFormat(formatValue, out var tileFormatToken))
        {
            return CreateExceptionReport(
                "InvalidParameterValue",
                "format",
                "FORMAT must be image/png or image/jpeg.",
                StatusCodes.Status400BadRequest);
        }

        if (!string.Equals(tileMatrixSet, TileMatrixSet, StringComparison.OrdinalIgnoreCase))
        {
            return CreateExceptionReport(
                "InvalidParameterValue",
                "TileMatrixSet",
                $"Only TILEMATRIXSET={TileMatrixSet} is supported.",
                StatusCodes.Status400BadRequest);
        }

        if (!int.TryParse(tileMatrix, NumberStyles.None, CultureInfo.InvariantCulture, out var level) ||
            level < 0 ||
            level > MaxZoom)
        {
            return CreateExceptionReport(
                "InvalidParameterValue",
                "TileMatrix",
                $"TILEMATRIX must be an integer from 0 to {MaxZoom}.",
                StatusCodes.Status400BadRequest);
        }

        var matrixWidth = 1 << level;
        if (!int.TryParse(tileRow, NumberStyles.None, CultureInfo.InvariantCulture, out var row) ||
            row < 0 ||
            row >= matrixWidth)
        {
            return CreateExceptionReport(
                "InvalidParameterValue",
                "TileRow",
                "TILEROW is outside the TileMatrix bounds.",
                StatusCodes.Status400BadRequest);
        }

        if (!int.TryParse(tileCol, NumberStyles.None, CultureInfo.InvariantCulture, out var col) ||
            col < 0 ||
            col >= matrixWidth)
        {
            return CreateExceptionReport(
                "InvalidParameterValue",
                "TileCol",
                "TILECOL is outside the TileMatrix bounds.",
                StatusCodes.Status400BadRequest);
        }

        return await tileHandler.GetImageTileAsync(
            context,
            layerId,
            level,
            row,
            col,
            tileFormatToken,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IResult> HandleGetFeatureInfoAsync(
        HttpContext context,
        int layerId,
        string advertisedLayerIdentifier,
        CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        if (!RequireQueryValue(query, "VERSION", out var version, out var error) ||
            !RequireQueryValue(query, "LAYER", out var layerValue, out error) ||
            !RequireQueryValue(query, "TILEMATRIXSET", out var tileMatrixSet, out error) ||
            !RequireQueryValue(query, "TILEMATRIX", out var tileMatrix, out error) ||
            !RequireQueryValue(query, "TILEROW", out var tileRow, out error) ||
            !RequireQueryValue(query, "TILECOL", out var tileCol, out error) ||
            !RequireQueryValue(query, "I", out var iValue, out error) ||
            !RequireQueryValue(query, "J", out var jValue, out error))
        {
            return error!;
        }

        if (!string.Equals(version, Version, StringComparison.OrdinalIgnoreCase))
        {
            return CreateExceptionReport(
                "InvalidParameterValue", "version", $"VERSION must be {Version}.", StatusCodes.Status400BadRequest);
        }

        if (!IsLayerIdentifierMatch(layerValue, layerId, advertisedLayerIdentifier))
        {
            return CreateExceptionReport(
                "InvalidParameterValue", "layer", "Invalid LAYER parameter.", StatusCodes.Status400BadRequest);
        }

        if (!string.Equals(tileMatrixSet, TileMatrixSet, StringComparison.OrdinalIgnoreCase))
        {
            return CreateExceptionReport(
                "InvalidParameterValue", "TileMatrixSet", $"Only TILEMATRIXSET={TileMatrixSet} is supported.", StatusCodes.Status400BadRequest);
        }

        TryGetQueryValue(query, "INFOFORMAT", out var infoFormatValue);
        if (!TryResolveInfoFormat(infoFormatValue, out var infoFormat))
        {
            return CreateExceptionReport(
                "InvalidParameterValue", "infoFormat", "INFOFORMAT must be application/json or text/xml.", StatusCodes.Status400BadRequest);
        }

        if (!int.TryParse(tileMatrix, NumberStyles.None, CultureInfo.InvariantCulture, out var level) ||
            level < 0 || level > MaxZoom)
        {
            return CreateExceptionReport(
                "InvalidParameterValue", "TileMatrix", $"TILEMATRIX must be an integer from 0 to {MaxZoom}.", StatusCodes.Status400BadRequest);
        }

        var matrixWidth = 1 << level;
        if (!int.TryParse(tileRow, NumberStyles.None, CultureInfo.InvariantCulture, out var row) || row < 0 || row >= matrixWidth)
        {
            return CreateExceptionReport(
                "InvalidParameterValue", "TileRow", "TILEROW is outside the TileMatrix bounds.", StatusCodes.Status400BadRequest);
        }

        if (!int.TryParse(tileCol, NumberStyles.None, CultureInfo.InvariantCulture, out var col) || col < 0 || col >= matrixWidth)
        {
            return CreateExceptionReport(
                "InvalidParameterValue", "TileCol", "TILECOL is outside the TileMatrix bounds.", StatusCodes.Status400BadRequest);
        }

        if (!int.TryParse(iValue, NumberStyles.None, CultureInfo.InvariantCulture, out var i) || i < 0 || i >= TilePixels)
        {
            return CreateExceptionReport(
                "InvalidParameterValue", "i", $"I must be an integer from 0 to {TilePixels - 1}.", StatusCodes.Status400BadRequest);
        }

        if (!int.TryParse(jValue, NumberStyles.None, CultureInfo.InvariantCulture, out var j) || j < 0 || j >= TilePixels)
        {
            return CreateExceptionReport(
                "InvalidParameterValue", "j", $"J must be an integer from 0 to {TilePixels - 1}.", StatusCodes.Status400BadRequest);
        }

        var (worldX, worldY) = PixelToWebMercator(level, row, col, i, j);

        // Resolve participating rasters and read the pixel value at the computed point,
        // adapting to the same shared raster-store primitives as the identify operation.
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        PixelValueResult? pixel = null;
        if (ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is { } resolved)
        {
            var mergeStrategy = ImageServerV2Lookups.ResolveMergeStrategy(resolved.Resource, GetQueryString(query, "mosaicRule"));
            var selectionQuery = new RasterSelectionQuery
            {
                Geometry = ImageServerMosaicHelpers.CreatePointGeometry(worldX, worldY),
                GeometrySrid = 3857,
            };
            var selected = await rasterStore.QueryRastersAsync(layerId, selectionQuery, cancellationToken).ConfigureAwait(false);
            if (selected.Length == 1)
            {
                pixel = await rasterStore.IdentifyAsync(layerId, selected[0].Id, worldX, worldY, 3857, cancellationToken).ConfigureAwait(false);
            }
            else if (selected.Length > 1)
            {
                pixel = await rasterStore.IdentifyMosaicAsync(
                    layerId,
                    selected.Select(r => r.Id).ToArray(),
                    mergeStrategy,
                    worldX,
                    worldY,
                    3857,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return BuildFeatureInfoResult(infoFormat, advertisedLayerIdentifier, worldX, worldY, pixel);
    }

    // Maps a pixel (I, J) within a 256px WebMercatorQuad tile to its EPSG:3857 centre.
    private static (double X, double Y) PixelToWebMercator(int level, int row, int col, int i, int j)
    {
        var tileSpan = (2.0 * OriginShift) / (1 << level);
        var metersPerPixel = tileSpan / TilePixels;
        var tileMinX = -OriginShift + (col * tileSpan);
        var tileMaxY = OriginShift - (row * tileSpan);
        var x = tileMinX + ((i + 0.5) * metersPerPixel);
        var y = tileMaxY - ((j + 0.5) * metersPerPixel);
        return (x, y);
    }

    private static bool TryResolveInfoFormat(string? raw, out string infoFormat)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            string.Equals(raw, JsonInfoFormat, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "text/json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "application/geo+json", StringComparison.OrdinalIgnoreCase))
        {
            infoFormat = JsonInfoFormat;
            return true;
        }

        if (string.Equals(raw, XmlInfoFormat, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "application/xml", StringComparison.OrdinalIgnoreCase))
        {
            infoFormat = XmlInfoFormat;
            return true;
        }

        infoFormat = string.Empty;
        return false;
    }

    private static IResult BuildFeatureInfoResult(
        string infoFormat,
        string layerIdentifier,
        double x,
        double y,
        PixelValueResult? pixel)
    {
        var hasData = pixel is { HasData: true } p && p.BandValues.Count > 0;
        if (string.Equals(infoFormat, XmlInfoFormat, StringComparison.Ordinal))
        {
            return Results.Content(BuildFeatureInfoXml(layerIdentifier, x, y, pixel, hasData), "text/xml", Encoding.UTF8, StatusCodes.Status200OK);
        }

        return Results.Content(BuildFeatureInfoJson(layerIdentifier, x, y, pixel, hasData), JsonInfoFormat, Encoding.UTF8, StatusCodes.Status200OK);
    }

    private static string BuildFeatureInfoJson(string layerIdentifier, double x, double y, PixelValueResult? pixel, bool hasData)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("layer", layerIdentifier);
            writer.WriteStartObject("location");
            writer.WriteNumber("x", x);
            writer.WriteNumber("y", y);
            writer.WriteStartObject("spatialReference");
            writer.WriteNumber("wkid", 3857);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteBoolean("hasData", hasData);
            writer.WriteStartArray("bands");
            if (hasData && pixel is { } p)
            {
                foreach (var band in p.BandValues.OrderBy(static b => b.Key))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("band", band.Key);
                    WriteBandJsonValue(writer, band.Value);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteBandJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull("value");
                break;
            case double d:
                writer.WriteNumber("value", d);
                break;
            case float f:
                writer.WriteNumber("value", f);
                break;
            case int n:
                writer.WriteNumber("value", n);
                break;
            case long l:
                writer.WriteNumber("value", l);
                break;
            case short s:
                writer.WriteNumber("value", s);
                break;
            case byte b:
                writer.WriteNumber("value", b);
                break;
            default:
                writer.WriteString("value", Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static string BuildFeatureInfoXml(string layerIdentifier, double x, double y, PixelValueResult? pixel, bool hasData)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.Append("<FeatureInfoResponse layer=\"").Append(EscapeXml(layerIdentifier))
            .Append("\" hasData=\"").Append(hasData ? "true" : "false").AppendLine("\">");
        sb.Append("  <Location x=\"").Append(FormatCoordinate(x))
            .Append("\" y=\"").Append(FormatCoordinate(y)).AppendLine("\" srs=\"EPSG:3857\" />");
        if (hasData && pixel is { } p)
        {
            foreach (var band in p.BandValues.OrderBy(static b => b.Key))
            {
                sb.Append("  <Band id=\"").Append(band.Key.ToString(CultureInfo.InvariantCulture))
                    .Append("\" value=\"").Append(EscapeXml(Convert.ToString(band.Value, CultureInfo.InvariantCulture) ?? string.Empty))
                    .AppendLine("\" />");
            }
        }

        sb.AppendLine("</FeatureInfoResponse>");
        return sb.ToString();
    }

    private static string FormatCoordinate(double value)
        => value.ToString("0.############", CultureInfo.InvariantCulture);

    private static string? GetQueryString(IQueryCollection query, string name)
        => TryGetQueryValue(query, name, out var value) ? value : null;

    private async Task<IResult> CreateCapabilitiesAsync(
        HttpContext context,
        int layerId,
        string layerIdentifier,
        CancellationToken cancellationToken)
    {
        // Advertise a TIME dimension when the layer's rasters carry acquisition dates;
        // GetTile/GetFeatureInfo already honour a TIME parameter via the shared tile pipeline.
        long?[]? timeExtent = null;
        var rasters = await rasterStore.ListRastersAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (rasters.Any(static r => r.AcquisitionDate.HasValue))
        {
            timeExtent = ImageServerMosaicHelpers.CreateTimeExtent(rasters);
        }

        var xml = BuildCapabilitiesXml(context, layerIdentifier, timeExtent);
        return Results.Content(xml, ContentType, Encoding.UTF8, StatusCodes.Status200OK);
    }

    private static string BuildCapabilitiesXml(HttpContext context, string layerIdentifier, long?[]? timeExtent)
    {
        var wmtsBaseUrl = BuildWmtsBaseUrl(context);
        var escapedLayer = EscapeXml(layerIdentifier);
        var escapedBaseUrl = EscapeXml(wmtsBaseUrl);
        var escapedTemplate =
            $"{escapedBaseUrl}/{{Layer}}/{{Style}}/{{TileMatrixSet}}/{{TileMatrix}}/{{TileRow}}/{{TileCol}}.png";
        var escapedJpegTemplate =
            $"{escapedBaseUrl}/{{Layer}}/{{Style}}/{{TileMatrixSet}}/{{TileMatrix}}/{{TileRow}}/{{TileCol}}.jpg";

        var sb = new StringBuilder(8192);
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.AppendLine("""<Capabilities xmlns="http://www.opengis.net/wmts/1.0" xmlns:ows="http://www.opengis.net/ows/1.1" xmlns:xlink="http://www.w3.org/1999/xlink" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" version="1.0.0" xsi:schemaLocation="http://www.opengis.net/wmts/1.0 http://schemas.opengis.net/wmts/1.0/wmtsGetCapabilities_response.xsd">""");
        sb.AppendLine("  <ows:ServiceIdentification>");
        sb.AppendLine("    <ows:Title>Honua ImageServer WMTS</ows:Title>");
        sb.AppendLine("    <ows:ServiceType>OGC WMTS</ows:ServiceType>");
        sb.AppendLine("    <ows:ServiceTypeVersion>1.0.0</ows:ServiceTypeVersion>");
        sb.AppendLine("  </ows:ServiceIdentification>");
        sb.AppendLine("  <ows:OperationsMetadata>");
        AppendOperationMetadata(sb, "GetCapabilities", escapedBaseUrl);
        AppendOperationMetadata(sb, "GetTile", escapedBaseUrl);
        AppendOperationMetadata(sb, "GetFeatureInfo", escapedBaseUrl);
        sb.AppendLine("  </ows:OperationsMetadata>");
        sb.AppendLine("  <Contents>");
        sb.AppendLine("    <Layer>");
        sb.Append("      <ows:Title>ImageServer ").Append(escapedLayer).AppendLine("</ows:Title>");
        sb.Append("      <ows:Identifier>").Append(escapedLayer).AppendLine("</ows:Identifier>");
        sb.AppendLine("""      <Style isDefault="true">""");
        sb.AppendLine("        <ows:Identifier>default</ows:Identifier>");
        sb.AppendLine("      </Style>");
        sb.AppendLine("      <Format>image/png</Format>");
        sb.AppendLine("      <Format>image/jpeg</Format>");
        sb.AppendLine("      <InfoFormat>application/json</InfoFormat>");
        sb.AppendLine("      <InfoFormat>text/xml</InfoFormat>");
        AppendTimeDimension(sb, timeExtent);
        sb.AppendLine("      <TileMatrixSetLink>");
        sb.AppendLine("        <TileMatrixSet>WebMercatorQuad</TileMatrixSet>");
        sb.AppendLine("      </TileMatrixSetLink>");
        sb.Append("      <ResourceURL format=\"image/png\" resourceType=\"tile\" template=\"")
            .Append(escapedTemplate)
            .AppendLine("\" />");
        sb.Append("      <ResourceURL format=\"image/jpeg\" resourceType=\"tile\" template=\"")
            .Append(escapedJpegTemplate)
            .AppendLine("\" />");
        sb.AppendLine("    </Layer>");
        sb.AppendLine("    <TileMatrixSet>");
        sb.AppendLine("      <ows:Identifier>WebMercatorQuad</ows:Identifier>");
        sb.AppendLine("      <ows:SupportedCRS>urn:ogc:def:crs:EPSG::3857</ows:SupportedCRS>");

        for (var level = 0; level <= MaxZoom; level++)
        {
            var matrixWidth = 1 << level;
            sb.AppendLine("      <TileMatrix>");
            sb.Append("        <ows:Identifier>").Append(level.ToString(CultureInfo.InvariantCulture)).AppendLine("</ows:Identifier>");
            sb.Append("        <ScaleDenominator>")
                .Append(FormatScaleDenominator(ScaleDenominator0 / matrixWidth))
                .AppendLine("</ScaleDenominator>");
            sb.AppendLine("        <TopLeftCorner>-20037508.342789244 20037508.342789244</TopLeftCorner>");
            sb.AppendLine("        <TileWidth>256</TileWidth>");
            sb.AppendLine("        <TileHeight>256</TileHeight>");
            sb.Append("        <MatrixWidth>").Append(matrixWidth.ToString(CultureInfo.InvariantCulture)).AppendLine("</MatrixWidth>");
            sb.Append("        <MatrixHeight>").Append(matrixWidth.ToString(CultureInfo.InvariantCulture)).AppendLine("</MatrixHeight>");
            sb.AppendLine("      </TileMatrix>");
        }

        sb.AppendLine("    </TileMatrixSet>");
        sb.AppendLine("  </Contents>");
        sb.AppendLine("</Capabilities>");
        return sb.ToString();
    }

    private static void AppendTimeDimension(StringBuilder sb, long?[]? timeExtent)
    {
        if (timeExtent is not [{ } minMs, { } maxMs])
        {
            return;
        }

        var min = FormatIso8601(minMs);
        var max = FormatIso8601(maxMs);
        sb.AppendLine("      <Dimension>");
        sb.AppendLine("        <ows:Identifier>TIME</ows:Identifier>");
        sb.AppendLine("        <ows:UOM>ISO8601</ows:UOM>");
        sb.Append("        <Default>").Append(max).AppendLine("</Default>");
        sb.Append("        <Value>").Append(min).Append('/').Append(max).AppendLine("</Value>");
        sb.AppendLine("      </Dimension>");
    }

    private static string FormatIso8601(long unixMilliseconds)
        => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds)
            .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static void AppendOperationMetadata(StringBuilder sb, string operationName, string href)
    {
        sb.Append("    <ows:Operation name=\"").Append(operationName).AppendLine("\">");
        sb.AppendLine("      <ows:DCP>");
        sb.AppendLine("        <ows:HTTP>");
        sb.Append("          <ows:Get xlink:href=\"").Append(href).AppendLine("\">");
        sb.AppendLine("            <ows:Constraint name=\"GetEncoding\">");
        sb.AppendLine("              <ows:AllowedValues>");
        sb.AppendLine("                <ows:Value>KVP</ows:Value>");
        sb.AppendLine("                <ows:Value>REST</ows:Value>");
        sb.AppendLine("              </ows:AllowedValues>");
        sb.AppendLine("            </ows:Constraint>");
        sb.AppendLine("          </ows:Get>");
        sb.AppendLine("        </ows:HTTP>");
        sb.AppendLine("      </ows:DCP>");
        sb.AppendLine("    </ows:Operation>");
    }

    private static string BuildWmtsBaseUrl(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var wmtsIndex = path.IndexOf("/WMTS", StringComparison.OrdinalIgnoreCase);
        var wmtsPath = wmtsIndex >= 0
            ? path[..(wmtsIndex + "/WMTS".Length)]
            : path.TrimEnd('/');

        return $"{BaseUrlResolver.GetBaseUrl(context)}{wmtsPath}";
    }

    private static bool RequireQueryValue(
        IQueryCollection query,
        string name,
        out string value,
        out IResult? error)
    {
        if (!TryGetQueryValue(query, name, out value) || string.IsNullOrWhiteSpace(value))
        {
            error = CreateExceptionReport(
                "MissingParameterValue",
                name,
                $"{name} parameter is required.",
                StatusCodes.Status400BadRequest);
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryGetQueryValue(IQueryCollection query, string name, out string value)
    {
        foreach (var pair in query)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value.ToString();
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryDecodeTileColumn(string segment, out string tileCol, out string format)
    {
        tileCol = string.Empty;
        format = string.Empty;

        var extensionIndex = segment.LastIndexOf('.');
        if (extensionIndex <= 0 || extensionIndex == segment.Length - 1)
        {
            return false;
        }

        if (!TryDecodeSegment(segment[..extensionIndex], out tileCol))
        {
            return false;
        }

        var extension = segment[(extensionIndex + 1)..];
        if (string.Equals(extension, "png", StringComparison.OrdinalIgnoreCase))
        {
            format = PngFormat;
            return true;
        }

        if (string.Equals(extension, "jpg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "jpeg", StringComparison.OrdinalIgnoreCase))
        {
            format = JpegFormat;
            return true;
        }

        format = extension;
        return true;
    }

    private static bool TryDecodeSegment(string segment, out string value)
    {
        try
        {
            value = WebUtility.UrlDecode(segment);
            return value is not null;
        }
        catch (FormatException)
        {
            value = string.Empty;
            return false;
        }
    }

    private static bool IsLayerIdentifierMatch(string value, int layerId, string advertisedLayerIdentifier)
        => string.Equals(value, advertisedLayerIdentifier, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, layerId.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

    private static string EscapeXml(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string FormatScaleDenominator(double value)
        => value.ToString("0.###############", CultureInfo.InvariantCulture);

    private static IResult CreateExceptionReport(string code, string locator, string text, int statusCode)
    {
        var xml = new StringBuilder(512)
            .AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""")
            .Append("""<ows:ExceptionReport xmlns:ows="http://www.opengis.net/ows/1.1" version="1.0.0">""")
            .Append("<ows:Exception exceptionCode=\"").Append(EscapeXml(code)).Append("\" locator=\"").Append(EscapeXml(locator)).Append("\">")
            .Append("<ows:ExceptionText>").Append(EscapeXml(text)).Append("</ows:ExceptionText>")
            .Append("</ows:Exception>")
            .Append("</ows:ExceptionReport>")
            .ToString();

        return Results.Content(xml, ContentType, Encoding.UTF8, statusCode);
    }
}
