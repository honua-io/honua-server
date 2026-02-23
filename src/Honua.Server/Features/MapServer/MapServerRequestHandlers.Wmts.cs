// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.MapServer;

internal static partial class MapServerEndpoints
{
    private const double WebMercatorOrigin = 20037508.342787;
    private const int WmtsMaxZoom = 22;
    private const double PixelSizeMeters = 0.00028;

    /// <summary>
    /// Handle OGC WMTS requests (GetCapabilities, GetTile).
    /// </summary>
    private static async Task<IResult> HandleWmts(HttpContext context)
    {
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.MapServerEndpoints");

        try
        {
            var request = context.Request.Query;
            var service = request.TryGetValue("SERVICE", out var svc) ? svc.ToString() : null;
            var requestType = request.TryGetValue("REQUEST", out var req) ? req.ToString() : null;

            if (!string.Equals(service, "WMTS", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(service))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "SERVICE must be WMTS.");
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

            var svcDef = serviceResult.Resource!;
            var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(context, svcDef, ServiceProtocols.MapServer);
            if (protocolError is not null)
            {
                return protocolError;
            }

            var accessError = AccessPolicyHelpers.RequireAnyLayerAccess(context, svcDef.Layers, svcDef);
            if (accessError != null)
            {
                return accessError;
            }

            if (string.Equals(requestType, "GetTile", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleWmtsGetTile(context, serviceId, logger);
            }

            // Default to GetCapabilities
            MapServerLog.WmtsRequested(logger, serviceId, "GetCapabilities");
            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var xml = BuildWmtsCapabilities(svcDef, serviceId, baseUrl);
            return Results.Text(xml, "application/xml", Encoding.UTF8);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MapServerLog.WmtsFailed(logger, serviceId, ex.Message, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "WMTS request failed.");
        }
    }

    private static async Task<IResult> HandleWmtsGetTile(HttpContext context, string serviceId, ILogger logger)
    {
        MapServerLog.WmtsRequested(logger, serviceId, "GetTile");

        var query = context.Request.Query;
        var tileMatrixSet = query.TryGetValue("TILEMATRIXSET", out var tms) ? tms.ToString() : null;
        var tileMatrixValue = query.TryGetValue("TILEMATRIX", out var tm) ? tm.ToString() : null;
        var tileRowValue = query.TryGetValue("TILEROW", out var tr) ? tr.ToString() : null;
        var tileColValue = query.TryGetValue("TILECOL", out var tc) ? tc.ToString() : null;

        if (!string.IsNullOrWhiteSpace(tileMatrixSet) &&
            !string.Equals(tileMatrixSet, "WebMercatorQuad", StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Only WebMercatorQuad tile matrix set is supported.");
        }

        if (string.IsNullOrWhiteSpace(tileMatrixValue) ||
            !int.TryParse(tileMatrixValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
            string.IsNullOrWhiteSpace(tileRowValue) ||
            !int.TryParse(tileRowValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
            string.IsNullOrWhiteSpace(tileColValue) ||
            !int.TryParse(tileColValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "TILEMATRIX, TILEROW, and TILECOL are required integer parameters.");
        }

        // Delegate to the tile handler by setting route values
        context.Request.RouteValues["z"] = tileMatrixValue;
        context.Request.RouteValues["y"] = tileRowValue;
        context.Request.RouteValues["x"] = tileColValue;

        return await HandleTile(context);
    }

    private static string BuildWmtsCapabilities(ServiceDefinition service, string serviceId, string baseUrl)
    {
        var sb = new StringBuilder(4096);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<Capabilities xmlns=\"http://www.opengis.net/wmts/1.0\"");
        sb.AppendLine("  xmlns:ows=\"http://www.opengis.net/ows/1.1\"");
        sb.AppendLine("  xmlns:xlink=\"http://www.w3.org/1999/xlink\"");
        sb.AppendLine("  version=\"1.0.0\">");

        // ServiceIdentification
        sb.AppendLine("  <ows:ServiceIdentification>");
        sb.Append("    <ows:Title>").Append(EscapeXml(service.Name ?? serviceId)).AppendLine("</ows:Title>");
        if (!string.IsNullOrWhiteSpace(service.Description))
        {
            sb.Append("    <ows:Abstract>").Append(EscapeXml(service.Description)).AppendLine("</ows:Abstract>");
        }

        sb.AppendLine("    <ows:ServiceType>OGC WMTS</ows:ServiceType>");
        sb.AppendLine("    <ows:ServiceTypeVersion>1.0.0</ows:ServiceTypeVersion>");
        sb.AppendLine("  </ows:ServiceIdentification>");

        // ServiceProvider
        sb.AppendLine("  <ows:ServiceProvider>");
        sb.AppendLine("    <ows:ProviderName>Honua Server</ows:ProviderName>");
        sb.AppendLine("  </ows:ServiceProvider>");

        // OperationsMetadata
        var wmtsUrl = $"{baseUrl}/rest/services/{serviceId}/MapServer/WMTS";
        sb.AppendLine("  <ows:OperationsMetadata>");
        sb.AppendLine("    <ows:Operation name=\"GetCapabilities\">");
        sb.AppendLine("      <ows:DCP>");
        sb.AppendLine("        <ows:HTTP>");
        sb.Append("          <ows:Get xlink:href=\"").Append(EscapeXml(wmtsUrl)).AppendLine("\">");
        sb.AppendLine("            <ows:Constraint name=\"GetEncoding\">");
        sb.AppendLine("              <ows:AllowedValues><ows:Value>KVP</ows:Value></ows:AllowedValues>");
        sb.AppendLine("            </ows:Constraint>");
        sb.AppendLine("          </ows:Get>");
        sb.AppendLine("        </ows:HTTP>");
        sb.AppendLine("      </ows:DCP>");
        sb.AppendLine("    </ows:Operation>");
        sb.AppendLine("    <ows:Operation name=\"GetTile\">");
        sb.AppendLine("      <ows:DCP>");
        sb.AppendLine("        <ows:HTTP>");
        sb.Append("          <ows:Get xlink:href=\"").Append(EscapeXml(wmtsUrl)).AppendLine("\">");
        sb.AppendLine("            <ows:Constraint name=\"GetEncoding\">");
        sb.AppendLine("              <ows:AllowedValues><ows:Value>KVP</ows:Value></ows:AllowedValues>");
        sb.AppendLine("            </ows:Constraint>");
        sb.AppendLine("          </ows:Get>");
        sb.AppendLine("        </ows:HTTP>");
        sb.AppendLine("      </ows:DCP>");
        sb.AppendLine("    </ows:Operation>");
        sb.AppendLine("  </ows:OperationsMetadata>");

        // Contents
        sb.AppendLine("  <Contents>");

        // Layers
        var tileUrl = $"{baseUrl}/rest/services/{serviceId}/MapServer/tile/{{TileMatrix}}/{{TileRow}}/{{TileCol}}";
        var visibleLayers = service.Layers.Where(l => l.HasGeometry).ToArray();
        foreach (var layer in visibleLayers)
        {
            sb.AppendLine("    <Layer>");
            sb.Append("      <ows:Identifier>").Append(layer.Id.ToString(CultureInfo.InvariantCulture)).AppendLine("</ows:Identifier>");
            sb.Append("      <ows:Title>").Append(EscapeXml(layer.Name ?? "")).AppendLine("</ows:Title>");
            sb.AppendLine("      <Style isDefault=\"true\">");
            sb.AppendLine("        <ows:Identifier>default</ows:Identifier>");
            sb.AppendLine("      </Style>");
            sb.AppendLine("      <Format>image/png</Format>");
            sb.AppendLine("      <TileMatrixSetLink>");
            sb.AppendLine("        <TileMatrixSet>WebMercatorQuad</TileMatrixSet>");
            sb.AppendLine("      </TileMatrixSetLink>");
            sb.Append("      <ResourceURL format=\"image/png\" resourceType=\"tile\" template=\"").Append(EscapeXml(tileUrl)).AppendLine("\" />");
            sb.AppendLine("    </Layer>");
        }

        // TileMatrixSet
        sb.AppendLine("    <TileMatrixSet>");
        sb.AppendLine("      <ows:Identifier>WebMercatorQuad</ows:Identifier>");
        sb.AppendLine("      <ows:SupportedCRS>urn:ogc:def:crs:EPSG::3857</ows:SupportedCRS>");

        for (var z = 0; z <= WmtsMaxZoom; z++)
        {
            var matrixSize = 1 << z;
            var cellSize = 2.0 * WebMercatorOrigin / (TileSize * matrixSize);
            var scaleDenominator = cellSize / PixelSizeMeters;

            sb.AppendLine("      <TileMatrix>");
            sb.Append("        <ows:Identifier>").Append(z.ToString(CultureInfo.InvariantCulture)).AppendLine("</ows:Identifier>");
            sb.Append("        <ScaleDenominator>").Append(scaleDenominator.ToString("F6", CultureInfo.InvariantCulture)).AppendLine("</ScaleDenominator>");
            sb.Append("        <TopLeftCorner>").Append((-WebMercatorOrigin).ToString("F6", CultureInfo.InvariantCulture)).Append(' ').Append(WebMercatorOrigin.ToString("F6", CultureInfo.InvariantCulture)).AppendLine("</TopLeftCorner>");
            sb.Append("        <TileWidth>").Append(TileSize.ToString(CultureInfo.InvariantCulture)).AppendLine("</TileWidth>");
            sb.Append("        <TileHeight>").Append(TileSize.ToString(CultureInfo.InvariantCulture)).AppendLine("</TileHeight>");
            sb.Append("        <MatrixWidth>").Append(matrixSize.ToString(CultureInfo.InvariantCulture)).AppendLine("</MatrixWidth>");
            sb.Append("        <MatrixHeight>").Append(matrixSize.ToString(CultureInfo.InvariantCulture)).AppendLine("</MatrixHeight>");
            sb.AppendLine("      </TileMatrix>");
        }

        sb.AppendLine("    </TileMatrixSet>");
        sb.AppendLine("  </Contents>");
        sb.AppendLine("</Capabilities>");

        return sb.ToString();
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }
}
