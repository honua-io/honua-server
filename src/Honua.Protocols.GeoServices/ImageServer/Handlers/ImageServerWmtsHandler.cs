// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using Honua.Infrastructure.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

internal sealed class ImageServerWmtsHandler(ImageServerTileHandler tileHandler)
{
    private const int MaxZoom = 22;
    private const string ContentType = "application/xml";
    private const string TileMatrixSet = "WebMercatorQuad";
    private const string Version = "1.0.0";
    private const string TileFormat = "image/png";
    private const double ScaleDenominator0 = 559082264.0287178;

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

            return CreateCapabilities(context, advertisedLayerIdentifier);
        }

        if (string.Equals(request, "GetTile", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleGetTileAsync(
                context,
                layerId,
                advertisedLayerIdentifier,
                cancellationToken).ConfigureAwait(false);
        }

        return CreateExceptionReport(
            "OperationNotSupported",
            "request",
            "Only GetCapabilities and GetTile are supported for ImageServer WMTS.",
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
            return CreateCapabilities(context, advertisedLayerIdentifier);
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

        if (!string.Equals(formatValue, TileFormat, StringComparison.OrdinalIgnoreCase))
        {
            return CreateExceptionReport(
                "InvalidParameterValue",
                "format",
                "Only FORMAT=image/png is supported.",
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
            "png",
            cancellationToken).ConfigureAwait(false);
    }

    private static IResult CreateCapabilities(HttpContext context, string layerIdentifier)
    {
        var xml = BuildCapabilitiesXml(context, layerIdentifier);
        return Results.Content(xml, ContentType, Encoding.UTF8, StatusCodes.Status200OK);
    }

    private static string BuildCapabilitiesXml(HttpContext context, string layerIdentifier)
    {
        var wmtsBaseUrl = BuildWmtsBaseUrl(context);
        var escapedLayer = EscapeXml(layerIdentifier);
        var escapedBaseUrl = EscapeXml(wmtsBaseUrl);
        var escapedTemplate =
            $"{escapedBaseUrl}/{{Layer}}/{{Style}}/{{TileMatrixSet}}/{{TileMatrix}}/{{TileRow}}/{{TileCol}}.png";

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
        sb.AppendLine("  </ows:OperationsMetadata>");
        sb.AppendLine("  <Contents>");
        sb.AppendLine("    <Layer>");
        sb.Append("      <ows:Title>ImageServer ").Append(escapedLayer).AppendLine("</ows:Title>");
        sb.Append("      <ows:Identifier>").Append(escapedLayer).AppendLine("</ows:Identifier>");
        sb.AppendLine("""      <Style isDefault="true">""");
        sb.AppendLine("        <ows:Identifier>default</ows:Identifier>");
        sb.AppendLine("      </Style>");
        sb.AppendLine("      <Format>image/png</Format>");
        sb.AppendLine("      <TileMatrixSetLink>");
        sb.AppendLine("        <TileMatrixSet>WebMercatorQuad</TileMatrixSet>");
        sb.AppendLine("      </TileMatrixSetLink>");
        sb.Append("      <ResourceURL format=\"image/png\" resourceType=\"tile\" template=\"")
            .Append(escapedTemplate)
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
            format = TileFormat;
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
