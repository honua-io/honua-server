// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Honua.Server.Features.Protocols.Ogc.Classic;
using Microsoft.Extensions.DependencyInjection;
using static Honua.Server.Features.Infrastructure.Rendering.RasterMapRenderingPipeline;
using static Honua.Server.Features.Protocols.Ogc.Classic.OgcClassicRequestHelpers;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wms;

internal static partial class WmsRequestHandlers
{
    private static void AppendWmsCiteDimensions(StringBuilder sb, WmsLayer layer, string indent, bool isWms111)
    {
        var definition = GetCiteWmsDimensionDefinition(layer);
        if (definition is null)
        {
            return;
        }

        if (isWms111)
        {
            sb.Append(indent)
                .Append("<Dimension name=\"")
                .Append(EscapeXml(definition.Value.Name))
                .Append("\" units=\"")
                .Append(EscapeXml(definition.Value.Units))
                .Append('"');

            if (!string.IsNullOrWhiteSpace(definition.Value.UnitSymbol))
            {
                sb.Append(" unitSymbol=\"")
                    .Append(EscapeXml(definition.Value.UnitSymbol!))
                    .Append('"');
            }

            sb.AppendLine(" />");
            sb.Append(indent)
                .Append("<Extent name=\"")
                .Append(EscapeXml(definition.Value.Name))
                .Append('"');

            if (!string.IsNullOrWhiteSpace(definition.Value.Default))
            {
                sb.Append(" default=\"")
                    .Append(EscapeXml(definition.Value.Default!))
                    .Append('"');
            }

            sb.Append(" nearestValue=\"")
                .Append(definition.Value.NearestValue ? "1" : "0")
                .Append("\">")
                .Append(EscapeXml(definition.Value.Extent))
                .AppendLine("</Extent>");
            return;
        }

        sb.Append(indent)
            .Append("<Dimension name=\"")
            .Append(EscapeXml(definition.Value.Name))
            .Append("\" units=\"")
            .Append(EscapeXml(definition.Value.Units))
            .Append('"');

        if (!string.IsNullOrWhiteSpace(definition.Value.UnitSymbol))
        {
            sb.Append(" unitSymbol=\"")
                .Append(EscapeXml(definition.Value.UnitSymbol!))
                .Append('"');
        }

        sb.Append(" multipleValues=\"")
            .Append(definition.Value.MultipleValues ? "true" : "false")
            .Append("\" nearestValue=\"")
            .Append(definition.Value.NearestValue ? "true" : "false")
            .Append('"');

        if (!string.IsNullOrWhiteSpace(definition.Value.Default))
        {
            sb.Append(" default=\"")
                .Append(EscapeXml(definition.Value.Default!))
                .Append('"');
        }

        if (!string.IsNullOrWhiteSpace(definition.Value.Current))
        {
            sb.Append(" current=\"")
                .Append(EscapeXml(definition.Value.Current!))
                .Append('"');
        }

        sb.Append('>')
            .Append(EscapeXml(definition.Value.Extent))
            .AppendLine("</Dimension>");
    }

    private static async Task AppendWmsTemporalDimensionAsync(
        HttpContext context,
        StringBuilder sb,
        WmsLayer layer,
        string indent,
        bool isWms111)
    {
        // CITE Autos has its own hardcoded "time" dimension already emitted by
        // AppendWmsCiteDimensions; do not duplicate.
        if (IsCiteLayerNamed(layer, CiteAutosLayerTitle))
        {
            return;
        }

        // Gate temporal dimension emission on the V2 opt-in resolver: a layer carries a
        // time dimension only when its Temporal extension declares a StartTimeField that
        // resolves to a Date/DateTime schema field. Matches the GetMap path (see
        // TryParseWmsLayerTemporalFilters) so capabilities and request validation share
        // one definition of "time-aware".
        if (!TemporalExtentHelpers.TryResolveOptInTemporalFieldsV2(layer.Resource, out _))
        {
            return;
        }

        var featureReader = context.RequestServices.GetService<IFeatureReader>();
        if (featureReader is null)
        {
            return;
        }

        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var range = await TemporalExtentHelpers.TryResolveTemporalRangeV2Async(
            layer.Resource,
            layer.StorageLayerId,
            featureReader,
            cancellationToken).ConfigureAwait(false);
        if (range is null || !range.Value.HasExtent || range.Value.Min is null || range.Value.Max is null)
        {
            return;
        }

        var min = FormatWmsTemporalInstant(range.Value.Min.Value);
        var max = FormatWmsTemporalInstant(range.Value.Max.Value);
        var extent = $"{min}/{max}/PT0S";

        if (isWms111)
        {
            sb.Append(indent)
                .Append("<Dimension name=\"time\" units=\"ISO8601\" />")
                .AppendLine();
            sb.Append(indent)
                .Append("<Extent name=\"time\" default=\"")
                .Append(EscapeXml(max))
                .Append("\" nearestValue=\"1\">")
                .Append(EscapeXml(extent))
                .AppendLine("</Extent>");
            return;
        }

        sb.Append(indent)
            .Append("<Dimension name=\"time\" units=\"ISO8601\" multipleValues=\"false\" nearestValue=\"true\" default=\"")
            .Append(EscapeXml(max))
            .Append("\">")
            .Append(EscapeXml(extent))
            .AppendLine("</Dimension>");
    }

    private static string FormatWmsTemporalInstant(DateTimeOffset value)
        => TemporalExtentHelpers.FormatOgcTemporalValue(value);

    private static void AppendWmsOnlineResource(StringBuilder sb, string indent, string href, bool isWms111)
    {
        sb.Append(indent).Append("<OnlineResource ");
        if (isWms111)
        {
            sb.Append("xmlns:xlink=\"http://www.w3.org/1999/xlink\" xlink:type=\"simple\" ");
        }

        sb.Append("xlink:href=\"")
            .Append(EscapeXml(href))
            .AppendLine("\" />");
    }

    private static CiteWmsDimensionDefinition? GetCiteWmsDimensionDefinition(WmsLayer layer)
    {
        if (IsCiteLayerNamed(layer, CiteTerrainLayerTitle))
        {
            return new CiteWmsDimensionDefinition(
                Name: "elevation",
                Units: "CRS:88",
                UnitSymbol: "m",
                MultipleValues: true,
                NearestValue: false,
                Default: CiteTerrainDefaultElevation,
                Current: null,
                Extent: "0/425/1");
        }

        if (IsCiteLayerNamed(layer, CiteLakesLayerTitle))
        {
            return new CiteWmsDimensionDefinition(
                Name: "elevation",
                Units: "CRS:88",
                UnitSymbol: "m",
                MultipleValues: false,
                NearestValue: true,
                Default: "500",
                Current: null,
                Extent: "500,490,480");
        }

        if (IsCiteLayerNamed(layer, CiteAutosLayerTitle))
        {
            return new CiteWmsDimensionDefinition(
                Name: "time",
                Units: "ISO8601",
                UnitSymbol: null,
                MultipleValues: true,
                NearestValue: true,
                Default: CiteAutosDefaultTime,
                Current: null,
                Extent: CiteAutosExtent);
        }

        return null;
    }

    private static async Task<string> BuildWmsCapabilities(
        HttpContext context,
        MetadataV2Service service,
        IReadOnlyList<WmsLayer> accessibleLayers,
        string serviceId,
        string baseUrl,
        string version)
    {
        var isWms111 = IsWms111Version(version);
        var responseVersion = isWms111 ? Wms111Version : Wms13Version;
        var crsElementName = isWms111 ? "SRS" : "CRS";
        var normalizedBaseUrl = baseUrl.TrimEnd('/');
        var wmsEndpoint = $"{normalizedBaseUrl}/rest/services/{serviceId}/MapServer/WMS";
        var wmsUrlPrefix = $"{wmsEndpoint}?";
        var metadataUrl = $"{wmsEndpoint}?SERVICE=WMS&REQUEST=GetCapabilities&VERSION={responseVersion}";
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var coordinateTransformService = context.RequestServices.GetService<ICoordinateTransformService>();
        var serviceTitle = service.Metadata.Title ?? service.Metadata.Name ?? serviceId;
        var serviceAbstract = service.Metadata.Description ?? "Honua WMS service";

        var sb = new StringBuilder(8192);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        if (isWms111)
        {
            sb.Append("<!DOCTYPE WMT_MS_Capabilities SYSTEM \"")
                .Append(Wms111CapabilitiesDtd)
                .AppendLine("\">");
            sb.Append("<WMT_MS_Capabilities ")
                .Append("version=\"1.1.1\"")
                .AppendLine(">");
        }
        else
        {
            sb.Append("<WMS_Capabilities ")
                .Append("xmlns=\"http://www.opengis.net/wms\" ")
                .Append("xmlns:xlink=\"http://www.w3.org/1999/xlink\" ")
                .Append("xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" ")
                .Append("version=\"1.3.0\" xsi:schemaLocation=\"")
                .Append(WmsCapabilitiesSchemaLocation)
                .AppendLine("\">");
        }

        sb.AppendLine("  <Service>");
        sb.AppendLine("    <Name>WMS</Name>");
        sb.Append("    <Title>").Append(EscapeXml(serviceTitle)).AppendLine("</Title>");
        sb.Append("    <Abstract>").Append(EscapeXml(serviceAbstract)).AppendLine("</Abstract>");
        sb.AppendLine("    <KeywordList>");
        sb.AppendLine("      <Keyword>WMS</Keyword>");
        sb.AppendLine("      <Keyword>OGC</Keyword>");
        sb.Append("      <Keyword>").Append(EscapeXml(service.Metadata.Name ?? serviceId)).AppendLine("</Keyword>");
        sb.AppendLine("    </KeywordList>");
        AppendWmsOnlineResource(sb, "    ", wmsEndpoint, isWms111);
        sb.AppendLine("    <ContactInformation>");
        sb.AppendLine("      <ContactPersonPrimary>");
        sb.AppendLine("        <ContactPerson>Honua Support</ContactPerson>");
        sb.AppendLine("        <ContactOrganization>Honua</ContactOrganization>");
        sb.AppendLine("      </ContactPersonPrimary>");
        sb.AppendLine("      <ContactPosition>Support Engineer</ContactPosition>");
        sb.AppendLine("      <ContactAddress>");
        sb.AppendLine("        <AddressType>postal</AddressType>");
        sb.AppendLine("        <Address>1 Honua Way</Address>");
        sb.AppendLine("        <City>Honolulu</City>");
        sb.AppendLine("        <StateOrProvince>HI</StateOrProvince>");
        sb.AppendLine("        <PostCode>96815</PostCode>");
        sb.AppendLine("        <Country>US</Country>");
        sb.AppendLine("      </ContactAddress>");
        sb.AppendLine("      <ContactVoiceTelephone>+1-555-0100</ContactVoiceTelephone>");
        sb.AppendLine("      <ContactFacsimileTelephone>+1-555-0101</ContactFacsimileTelephone>");
        sb.AppendLine("      <ContactElectronicMailAddress>support@honua.local</ContactElectronicMailAddress>");
        sb.AppendLine("    </ContactInformation>");
        sb.AppendLine("  </Service>");

        sb.AppendLine("  <Capability>");
        sb.AppendLine("    <Request>");

        sb.AppendLine("      <GetCapabilities>");
        sb.Append("        <Format>")
            .Append(isWms111 ? Wms111CapabilitiesMimeType : WmsCapabilitiesMimeType)
            .AppendLine("</Format>");
        sb.AppendLine("        <DCPType>");
        sb.AppendLine("          <HTTP>");
        sb.AppendLine("            <Get>");
        AppendWmsOnlineResource(sb, "              ", wmsUrlPrefix, isWms111);
        sb.AppendLine("            </Get>");
        sb.AppendLine("          </HTTP>");
        sb.AppendLine("        </DCPType>");
        sb.AppendLine("      </GetCapabilities>");

        sb.AppendLine("      <GetMap>");
        sb.AppendLine("        <Format>image/png</Format>");
        sb.AppendLine("        <Format>image/jpeg</Format>");
        sb.AppendLine("        <DCPType>");
        sb.AppendLine("          <HTTP>");
        sb.AppendLine("            <Get>");
        AppendWmsOnlineResource(sb, "              ", wmsUrlPrefix, isWms111);
        sb.AppendLine("            </Get>");
        sb.AppendLine("          </HTTP>");
        sb.AppendLine("        </DCPType>");
        sb.AppendLine("      </GetMap>");

        sb.AppendLine("      <GetFeatureInfo>");
        sb.AppendLine("        <Format>text/plain</Format>");
        sb.AppendLine("        <Format>application/json</Format>");
        sb.AppendLine("        <DCPType>");
        sb.AppendLine("          <HTTP>");
        sb.AppendLine("            <Get>");
        AppendWmsOnlineResource(sb, "              ", wmsUrlPrefix, isWms111);
        sb.AppendLine("            </Get>");
        sb.AppendLine("          </HTTP>");
        sb.AppendLine("        </DCPType>");
        sb.AppendLine("      </GetFeatureInfo>");

        sb.AppendLine("    </Request>");
        sb.AppendLine("    <Exception>");
        if (isWms111)
        {
            sb.Append("      <Format>")
                .Append(WmsSeXmlExceptionMimeType)
                .AppendLine("</Format>");
        }
        else
        {
            sb.AppendLine("      <Format>XML</Format>");
        }
        sb.AppendLine("    </Exception>");

        sb.AppendLine("    <Layer>");
        sb.Append("      <Title>").Append(EscapeXml(serviceTitle)).AppendLine("</Title>");
        sb.Append("      <Abstract>").Append(EscapeXml(service.Metadata.Description ?? "Honua WMS root layer")).AppendLine("</Abstract>");
        sb.Append("      <").Append(crsElementName).AppendLine(">EPSG:4326</" + crsElementName + ">");
        sb.Append("      <").Append(crsElementName).AppendLine(">EPSG:3857</" + crsElementName + ">");
        if (!isWms111)
        {
            sb.AppendLine("      <CRS>CRS:84</CRS>");
        }

        // Root-layer bbox: union of per-resource CRS84 bboxes when V2 carries any,
        // otherwise fall back to world bounds. WMS clients rely on the root layer carrying
        // *some* extent to
        // bootstrap zoom-to-layer behavior, so the world fallback preserves that
        // contract until graphs carry per-resource bboxes.
        var rootExtent = await TryComputeRootWgs84ExtentAsync(
            accessibleLayers,
            coordinateTransformService,
            cancellationToken).ConfigureAwait(false) ?? WorldWgs84Extent;
        await AppendWmsGeographicBoundsAsync(context, sb, rootExtent, "      ", isWms111).ConfigureAwait(false);

        foreach (var layer in accessibleLayers)
        {
            var layerName = GetWmsLayerDisplayName(layer);
            var resourceTitle = layer.Resource.Metadata.Title ?? layer.Resource.Metadata.Name ?? layerName;
            var layerAbstract = layer.Resource.Metadata.Description ?? $"WMS layer {layerName}";

            sb.AppendLine("      <Layer queryable=\"1\">");
            sb.Append("        <Name>").Append(EscapeXml(layerName)).AppendLine("</Name>");
            sb.Append("        <Title>").Append(EscapeXml(resourceTitle)).AppendLine("</Title>");
            sb.Append("        <Abstract>").Append(EscapeXml(layerAbstract)).AppendLine("</Abstract>");
            sb.AppendLine("        <KeywordList>");
            sb.Append("          <Keyword>").Append(EscapeXml(layerName)).AppendLine("</Keyword>");
            sb.AppendLine("        </KeywordList>");
            sb.Append("        <").Append(crsElementName).AppendLine(">EPSG:4326</" + crsElementName + ">");
            sb.Append("        <").Append(crsElementName).AppendLine(">EPSG:3857</" + crsElementName + ">");
            if (!isWms111)
            {
                sb.AppendLine("        <CRS>CRS:84</CRS>");
            }

            var layerExtent = await TryGetLayerWgs84ExtentAsync(
                layer.Resource,
                coordinateTransformService,
                cancellationToken).ConfigureAwait(false) ?? rootExtent;
            await AppendWmsGeographicBoundsAsync(context, sb, layerExtent, "        ", isWms111).ConfigureAwait(false);

            AppendWmsCiteDimensions(sb, layer, "        ", isWms111);
            await AppendWmsTemporalDimensionAsync(context, sb, layer, "        ", isWms111).ConfigureAwait(false);

            sb.AppendLine("        <MetadataURL type=\"TC211\">");
            sb.AppendLine("          <Format>text/xml</Format>");
            AppendWmsOnlineResource(sb, "          ", metadataUrl, isWms111);
            sb.AppendLine("        </MetadataURL>");
            sb.AppendLine("        <Style>");
            sb.AppendLine("          <Name>default</Name>");
            sb.AppendLine("          <Title>Default style</Title>");
            sb.AppendLine("        </Style>");
            sb.AppendLine("      </Layer>");
        }

        sb.AppendLine("    </Layer>");
        sb.AppendLine("  </Capability>");
        sb.Append(isWms111 ? "</WMT_MS_Capabilities>" : "</WMS_Capabilities>").AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// CRS84 bbox for one V2 resource: reads <see cref="MetadataV2ResourceSpatial.Bbox"/>
    /// in the resource's spatial reference and reprojects to CRS84 via
    /// <see cref="OgcExtentTransformer.TryTransformExtentToCrs84Async"/> (which
    /// short-circuits the 4326 case). Returns null when the resource has no bbox or
    /// the transform fails — the caller falls back to the root extent.
    /// </summary>
    private static async Task<Wgs84Extent?> TryGetLayerWgs84ExtentAsync(
        MetadataV2Resource resource,
        ICoordinateTransformService? coordinateTransformService,
        CancellationToken cancellationToken)
    {
        var bbox = resource.ReadBbox();
        if (bbox is null)
        {
            return null;
        }

        var srid = resource.ReadSrid() ?? 4326;
        var transformed = await OgcExtentTransformer
            .TryTransformExtentToCrs84Async(
                bbox.West,
                bbox.South,
                bbox.East,
                bbox.North,
                srid,
                coordinateTransformService,
                cancellationToken)
            .ConfigureAwait(false);
        if (transformed is null)
        {
            return null;
        }

        return new Wgs84Extent(
            Math.Min(transformed.Value.MinLon, transformed.Value.MaxLon),
            Math.Min(transformed.Value.MinLat, transformed.Value.MaxLat),
            Math.Max(transformed.Value.MinLon, transformed.Value.MaxLon),
            Math.Max(transformed.Value.MinLat, transformed.Value.MaxLat));
    }

    /// <summary>
    /// Union of per-resource CRS84 bboxes. V2 carries no service-level extent, so the
    /// root <c>Layer</c>'s bbox is the bounding union of every accessible resource that
    /// declares a bbox. Returns null when no resource declares one, in which case
    /// capabilities omits the root extent (matches the WCS V2 port).
    /// </summary>
    private static async Task<Wgs84Extent?> TryComputeRootWgs84ExtentAsync(
        IReadOnlyList<WmsLayer> layers,
        ICoordinateTransformService? coordinateTransformService,
        CancellationToken cancellationToken)
    {
        Wgs84Extent? aggregate = null;
        foreach (var layer in layers)
        {
            var extent = await TryGetLayerWgs84ExtentAsync(layer.Resource, coordinateTransformService, cancellationToken)
                .ConfigureAwait(false);
            if (extent is null)
            {
                continue;
            }

            aggregate = aggregate is null
                ? extent
                : new Wgs84Extent(
                    Math.Min(aggregate.Value.MinLon, extent.Value.MinLon),
                    Math.Min(aggregate.Value.MinLat, extent.Value.MinLat),
                    Math.Max(aggregate.Value.MaxLon, extent.Value.MaxLon),
                    Math.Max(aggregate.Value.MaxLat, extent.Value.MaxLat));
        }

        return aggregate;
    }

    private static Task AppendWmsGeographicBoundsAsync(
        HttpContext context,
        StringBuilder sb,
        Wgs84Extent extent,
        string indent,
        bool isWms111)
    {
        _ = context; // reserved for future transform error reporting.
        if (isWms111)
        {
            AppendWms111LatLonBoundingBox(
                sb,
                extent.MinLon,
                extent.MinLat,
                extent.MaxLon,
                extent.MaxLat,
                indent);
            AppendWmsBoundingBox(
                sb,
                "EPSG:4326",
                extent.MinLon,
                extent.MinLat,
                extent.MaxLon,
                extent.MaxLat,
                indent,
                isWms111);
        }
        else
        {
            AppendWmsGeographicBoundingBox(
                sb,
                extent.MinLon,
                extent.MinLat,
                extent.MaxLon,
                extent.MaxLat,
                indent);
            AppendWmsBoundingBox(
                sb,
                "CRS:84",
                extent.MinLon,
                extent.MinLat,
                extent.MaxLon,
                extent.MaxLat,
                indent,
                isWms111);
            AppendWmsBoundingBox(
                sb,
                "EPSG:4326",
                extent.MinLon,
                extent.MinLat,
                extent.MaxLon,
                extent.MaxLat,
                indent,
                isWms111);
        }

        return Task.CompletedTask;
    }

    private static void AppendWmsGeographicBoundingBox(
        StringBuilder sb,
        double minX,
        double minY,
        double maxX,
        double maxY,
        string indent)
    {
        sb.Append(indent).AppendLine("<EX_GeographicBoundingBox>");
        sb.Append(indent).Append("  <westBoundLongitude>").Append(minX.ToString("F6", CultureInfo.InvariantCulture)).AppendLine("</westBoundLongitude>");
        sb.Append(indent).Append("  <eastBoundLongitude>").Append(maxX.ToString("F6", CultureInfo.InvariantCulture)).AppendLine("</eastBoundLongitude>");
        sb.Append(indent).Append("  <southBoundLatitude>").Append(minY.ToString("F6", CultureInfo.InvariantCulture)).AppendLine("</southBoundLatitude>");
        sb.Append(indent).Append("  <northBoundLatitude>").Append(maxY.ToString("F6", CultureInfo.InvariantCulture)).AppendLine("</northBoundLatitude>");
        sb.Append(indent).AppendLine("</EX_GeographicBoundingBox>");
    }

    private static void AppendWmsBoundingBox(
        StringBuilder sb,
        string crs,
        double minX,
        double minY,
        double maxX,
        double maxY,
        string indent,
        bool isWms111)
    {
        var outputMinX = minX;
        var outputMinY = minY;
        var outputMaxX = maxX;
        var outputMaxY = maxY;
        if (!isWms111 &&
            string.Equals(crs, "EPSG:4326", StringComparison.OrdinalIgnoreCase))
        {
            outputMinX = minY;
            outputMinY = minX;
            outputMaxX = maxY;
            outputMaxY = maxX;
        }

        sb.Append(indent)
            .Append("<BoundingBox ")
            .Append(isWms111 ? "SRS" : "CRS")
            .Append("=\"")
            .Append(EscapeXml(crs))
            .Append("\" minx=\"")
            .Append(outputMinX.ToString("F6", CultureInfo.InvariantCulture))
            .Append("\" miny=\"")
            .Append(outputMinY.ToString("F6", CultureInfo.InvariantCulture))
            .Append("\" maxx=\"")
            .Append(outputMaxX.ToString("F6", CultureInfo.InvariantCulture))
            .Append("\" maxy=\"")
            .Append(outputMaxY.ToString("F6", CultureInfo.InvariantCulture))
            .AppendLine("\" />");
    }

    private static void AppendWms111LatLonBoundingBox(
        StringBuilder sb,
        double minX,
        double minY,
        double maxX,
        double maxY,
        string indent)
    {
        sb.Append(indent)
            .Append("<LatLonBoundingBox minx=\"")
            .Append(minX.ToString("F6", CultureInfo.InvariantCulture))
            .Append("\" miny=\"")
            .Append(minY.ToString("F6", CultureInfo.InvariantCulture))
            .Append("\" maxx=\"")
            .Append(maxX.ToString("F6", CultureInfo.InvariantCulture))
            .Append("\" maxy=\"")
            .Append(maxY.ToString("F6", CultureInfo.InvariantCulture))
            .AppendLine("\" />");
    }

    /// <summary>CRS84 (lon/lat) bbox computed from a resource's bbox.</summary>
    private readonly record struct Wgs84Extent(double MinLon, double MinLat, double MaxLon, double MaxLat);

    /// <summary>World-wide CRS84 fallback used when no resource on the service declares a bbox.</summary>
    private static readonly Wgs84Extent WorldWgs84Extent = new(-180d, -90d, 180d, 90d);

    private readonly record struct CiteWmsDimensionDefinition(
        string Name,
        string Units,
        string? UnitSymbol,
        bool MultipleValues,
        bool NearestValue,
        string? Default,
        string? Current,
        string Extent);
}
