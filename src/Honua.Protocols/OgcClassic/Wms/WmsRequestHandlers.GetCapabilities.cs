// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Helpers;
using MetadataV2TemporalRange = Honua.Infrastructure.Helpers.TemporalExtentHelpers.MetadataV2TemporalRange;
using Honua.Infrastructure.Rendering;
using Honua.Infrastructure.Services;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Classic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Honua.Infrastructure.Rendering.RasterMapRenderingPipeline;
using static Honua.Protocols.Ogc.Classic.OgcClassicRequestHelpers;

namespace Honua.Protocols.Ogc.Classic.Wms;

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
                .Append(EscapeXml(
                    string.Equals(definition.Value.Name, "elevation", StringComparison.OrdinalIgnoreCase)
                        ? "EPSG:5703"
                        : definition.Value.Units))
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

    private static async Task<Dictionary<int, MetadataV2TemporalRange>> PrefetchTemporalRangesAsync(
        HttpContext context,
        IReadOnlyList<WmsLayer> layers,
        CancellationToken cancellationToken)
    {
        var featureReader = context.RequestServices.GetService<IFeatureReader>();
        if (featureReader is null)
        {
            return [];
        }

        // Identify time-aware layers (gate matches the GetMap/capabilities emission path).
        var temporalLayers = layers
            .Where(layer =>
                !IsCiteLayerNamed(layer, CiteAutosLayerTitle) &&
                TemporalExtentHelpers.TryResolveOptInTemporalFieldsV2(layer.Resource, out _))
            .ToArray();

        if (temporalLayers.Length == 0)
        {
            return [];
        }

        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Honua.Protocols.Ogc.Classic.Wms.WmsRequestHandlers");

        // Fan out DB aggregate queries concurrently; each is a MIN/MAX scan on one table.
        // Resolve each layer defensively: a single layer whose temporal column cannot be
        // aggregated (e.g. data stored in an encoding the cast cannot parse) must not 500
        // the whole GetCapabilities document — skip its TIME dimension and keep the rest
        // (honua-server#1911).
        var tasks = temporalLayers.Select(layer =>
            ResolveTemporalRangeSafeAsync(context, layer, featureReader, logger, cancellationToken));

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var dict = new Dictionary<int, MetadataV2TemporalRange>(temporalLayers.Length);
        for (var i = 0; i < temporalLayers.Length; i++)
        {
            if (results[i] is { } range)
            {
                dict[temporalLayers[i].StorageLayerId] = range;
            }
        }

        return dict;
    }

    private static async Task<Dictionary<int, (int Width, int Height)>> PrefetchLegendDimensionsAsync(
        HttpContext context,
        IReadOnlyList<WmsLayer> layers,
        CancellationToken cancellationToken)
    {
        var styleCatalog = context.RequestServices.GetService<ILayerStyleCatalog>();
        if (styleCatalog is null || layers.Count == 0)
        {
            return [];
        }

        // Fan out like the temporal prefetch above: each style lookup opens its own
        // connection, so the per-layer capabilities loop does not pay a sequential
        // round-trip per layer.
        var candidates = layers.Where(static layer => ResourceHasGeometry(layer.Resource)).ToArray();
        if (candidates.Length == 0)
        {
            return [];
        }

        var plans = await Task.WhenAll(candidates.Select(layer =>
            GetRasterStylePlanAsync(styleCatalog, layer.StorageLayerId, cancellationToken)))
            .ConfigureAwait(false);

        var dimensions = new Dictionary<int, (int Width, int Height)>();
        for (var i = 0; i < candidates.Length; i++)
        {
            if (!SupportsLegend(plans[i]))
            {
                continue;
            }

            var layer = candidates[i];
            var (entries, _) = BuildLegendEntries(
                plans[i],
                GetWmsLayerDisplayName(layer),
                RenderZoom.NotDerivable("the GetLegendGraphic request supplied no SCALE"));
            dimensions[layer.StorageLayerId] = LegendImageComposer.Measure(
                entries,
                DefaultLegendSwatchWidth,
                DefaultLegendSwatchHeight);
        }

        return dimensions;
    }

    private static async Task<MetadataV2TemporalRange?> ResolveTemporalRangeSafeAsync(
        HttpContext context,
        WmsLayer layer,
        IFeatureReader featureReader,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var layerReader = await ResolveLayerReaderAsync(context, layer, featureReader, cancellationToken)
                .ConfigureAwait(false);
            return await TemporalExtentHelpers.TryResolveTemporalRangeV2Async(
                layer.Resource,
                layer.StorageLayerId,
                layerReader,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally generic: a per-layer temporal resolution failure must degrade to no
            // TIME dimension rather than fail the whole capabilities document (see doc comment above).
            OgcClassicLog.TemporalExtentSkipped(
                logger,
                layer.StorageLayerId,
                GetWmsLayerDisplayName(layer),
                ex);
            return null;
        }
    }

    private const string ContinuousTemporalResolution = "0";

    private static void AppendWmsTemporalDimension(
        StringBuilder sb,
        WmsLayer layer,
        string indent,
        bool isWms111,
        Dictionary<int, MetadataV2TemporalRange> temporalRanges)
    {
        // CITE Autos has its own hardcoded "time" dimension already emitted by
        // AppendWmsCiteDimensions; do not duplicate.
        if (IsCiteLayerNamed(layer, CiteAutosLayerTitle))
        {
            return;
        }

        if (!temporalRanges.TryGetValue(layer.StorageLayerId, out var range) ||
            !range.HasExtent || range.Min is null || range.Max is null)
        {
            return;
        }

        var min = FormatWmsTemporalInstant(range.Min.Value);
        var max = FormatWmsTemporalInstant(range.Max.Value);
        // WMS 1.3.0 Annex C spells a continuous interval "min/max/0": the third element
        // is a resolution, and the literal 0 is what declares that every instant in the
        // interval is available. "PT0S" looks like the same statement in ISO 8601 but is
        // a zero-length duration, which is not a valid resolution. ArcGIS Pro rejects the
        // dimension outright and reports the layer as not time-enabled, while QGIS
        // tolerates it - so the spelling alone decided whether a client saw the time
        // dimension at all. See honua-server#5172.
        var extent = $"{min}/{max}/{ContinuousTemporalResolution}";

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
        string version)
    {
        var isWms111 = IsWms111Version(version);
        var crsElementName = isWms111 ? "SRS" : "CRS";
        var wmsEndpoint = BaseUrlResolver.PreserveToken(
            context.Request,
            GeoServicesServiceUrls.RequestedProtocol(context, "WMS"));
        var wmsUrlPrefix = $"{wmsEndpoint}{(wmsEndpoint.Contains('?') ? '&' : '?')}";
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var coordinateTransformService = context.RequestServices.GetService<ICoordinateTransformService>();
        var serviceTitle = service.Metadata.Title ?? service.EffectivePublicName;
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
        sb.Append("    <Name>")
            .Append(isWms111 ? "OGC:WMS" : "WMS")
            .AppendLine("</Name>");
        sb.Append("    <Title>").Append(EscapeXml(serviceTitle)).AppendLine("</Title>");
        sb.Append("    <Abstract>").Append(EscapeXml(serviceAbstract)).AppendLine("</Abstract>");
        AppendWmsKeywords(sb, service.Metadata.Keywords
            .Prepend(service.EffectivePublicName).Prepend("OGC").Prepend("WMS"), "    ");
        AppendWmsOnlineResource(sb, "    ", wmsEndpoint, isWms111);
        var contact = CatalogCapabilitiesXml.WmsContact(service.Metadata);
        if (contact is not null)
            sb.Append("    ").AppendLine(contact.ToString());
        sb.Append("    <Fees>").Append(EscapeXml(service.Metadata.Fees ?? string.Empty)).AppendLine("</Fees>");
        sb.Append("    <AccessConstraints>").Append(EscapeXml(service.Metadata.AccessConstraints ?? string.Empty)).AppendLine("</AccessConstraints>");
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
        sb.AppendLine("        <Format>application/vnd.ogc.gml</Format>");
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
        // Root-layer bbox: union of per-resource CRS84 bboxes when V2 carries any,
        // otherwise fall back to world bounds. WMS clients rely on the root layer carrying
        // *some* extent to
        // bootstrap zoom-to-layer behavior, so the world fallback preserves that
        // contract until graphs carry per-resource bboxes.
        var rootExtent = await TryComputeRootWgs84ExtentAsync(
            accessibleLayers,
            coordinateTransformService,
            cancellationToken).ConfigureAwait(false) ?? WorldWgs84Extent;
        await AppendWmsGeographicBoundsAsync(sb, new(rootExtent, "      ", isWms111, []), context).ConfigureAwait(false);

        // Pre-fetch temporal extent ranges for all time-aware layers concurrently so the
        // per-layer capabilities loop does not issue a sequential DB round-trip per layer.
        var temporalRanges = await PrefetchTemporalRangesAsync(
            context, accessibleLayers, cancellationToken).ConfigureAwait(false);

        var legendDimensions = await PrefetchLegendDimensionsAsync(
            context, accessibleLayers, cancellationToken).ConfigureAwait(false);

        foreach (var layer in accessibleLayers)
        {
            var layerName = GetWmsLayerDisplayName(layer);
            var layerMetadata = layer.Resource.Metadata;
            var resourceTitle = layerMetadata.Title ?? layerMetadata.Name ?? layerName;
            var layerAbstract = layerMetadata.Description ?? $"WMS layer {layerName}";

            sb.AppendLine("      <Layer queryable=\"1\">");
            sb.Append("        <Name>").Append(EscapeXml(layerName)).AppendLine("</Name>");
            sb.Append("        <Title>").Append(EscapeXml(resourceTitle)).AppendLine("</Title>");
            sb.Append("        <Abstract>").Append(EscapeXml(layerAbstract)).AppendLine("</Abstract>");
            AppendWmsKeywords(sb, layerMetadata.Keywords.Prepend(layerName), "        ");
            var supportedCrs = layer.Resource.Spatial?.SupportedCrs ?? [];
            var crsNames = supportedCrs.Count == 0
                ? new[] { "EPSG:4326", "EPSG:3857" }
                : supportedCrs.Select(crs => crs.Crs ?? $"EPSG:{crs.ResolveSrid()}").ToArray();
            crsNames = crsNames.Where(crs => !isWms111 || !string.Equals(crs, "CRS:84", StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var crs in crsNames.Distinct(StringComparer.OrdinalIgnoreCase))
                sb.Append("        <").Append(crsElementName).Append('>').Append(EscapeXml(crs))
                    .Append("</").Append(crsElementName).AppendLine(">");

            var layerExtent = await TryGetLayerWgs84ExtentAsync(
                layer.Resource, coordinateTransformService, cancellationToken).ConfigureAwait(false) ?? rootExtent;
            await AppendWmsGeographicBoundsAsync(sb, new(layerExtent, "        ", isWms111, crsNames), context).ConfigureAwait(false);

            // Preserve the CITE fixture dimensions (elevation on cite:Lakes and
            // time on cite:Autos) alongside the dynamic dimensions advertised by
            // the canonical resource metadata.
            AppendWmsCiteDimensions(sb, layer, "        ", isWms111);
            AppendWmsTemporalDimension(sb, layer, "        ", isWms111, temporalRanges);

            var metadataLink = layer.Resource.Metadata.Links.FirstOrDefault(link =>
                string.Equals(link.Rel, "metadata", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(link.Href));
            if (metadataLink is not null)
            {
                sb.AppendLine("        <MetadataURL type=\"TC211\">");
                sb.Append("          <Format>").Append(EscapeXml(metadataLink.Type ?? "application/xml")).AppendLine("</Format>");
                AppendWmsOnlineResource(sb, "          ", metadataLink.Href, isWms111);
                sb.AppendLine("        </MetadataURL>");
            }
            sb.AppendLine("        <Style>");
            sb.AppendLine("          <Name>default</Name>");
            sb.AppendLine("          <Title>Default style</Title>");
            if (legendDimensions.TryGetValue(layer.StorageLayerId, out var legendSize))
            {
                var legendHref =
                    $"{wmsUrlPrefix}SERVICE=WMS&VERSION={(isWms111 ? Wms111Version : Wms13Version)}"
                    + $"&REQUEST=GetLegendGraphic&LAYER={Uri.EscapeDataString(layerName)}"
                    + "&STYLE=default&FORMAT=image/png";
                if (isWms111)
                {
                    sb.Append("          <LegendURL width=\"")
                        .Append(legendSize.Width.ToString(CultureInfo.InvariantCulture))
                        .Append("\" height=\"")
                        .Append(legendSize.Height.ToString(CultureInfo.InvariantCulture))
                        .AppendLine("\">");
                }
                else
                {
                    sb.AppendLine("          <LegendURL>");
                }
                sb.AppendLine("            <Format>image/png</Format>");
                AppendWmsOnlineResource(sb, "            ", legendHref, isWms111);
                sb.AppendLine("          </LegendURL>");
            }

            sb.AppendLine("        </Style>");
            sb.AppendLine("      </Layer>");
        }

        sb.AppendLine("    </Layer>");
        sb.AppendLine("  </Capability>");
        sb.Append(isWms111 ? "</WMT_MS_Capabilities>" : "</WMS_Capabilities>").AppendLine();
        return sb.ToString();
    }

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

    private readonly record struct WmsGeographicBounds(
        Wgs84Extent Extent, string Indent, bool IsWms111, IReadOnlyList<string> DeclaredCrs);

    private static async Task AppendWmsGeographicBoundsAsync(
        StringBuilder builder, WmsGeographicBounds bounds, HttpContext context)
    {
        var extent = bounds.Extent;
        if (bounds.IsWms111)
            AppendWms111LatLonBoundingBox(builder, extent.MinLon, extent.MinLat, extent.MaxLon, extent.MaxLat, bounds.Indent);
        else
            AppendWmsGeographicBoundingBox(builder, extent.MinLon, extent.MinLat, extent.MaxLon, extent.MaxLat, bounds.Indent);

        var geographicExtent = new SkiaMapRenderer.RenderExtent(extent.MinLon, extent.MinLat, extent.MaxLon, extent.MaxLat);
        foreach (var crs in bounds.DeclaredCrs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryParseWmsCrs(crs, out var srid, out _))
                continue;
            var transformed = await TryTransformExtentAsync(context, geographicExtent, 4326, srid,
                TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context)).ConfigureAwait(false);
            if (!transformed.IsSuccess)
                continue;
            var projected = transformed.Extent;
            AppendWmsBoundingBox(builder, crs, projected.MinX, projected.MinY, projected.MaxX, projected.MaxY,
                bounds.Indent, bounds.IsWms111);
        }
    }

    private static void AppendWmsKeywords(StringBuilder builder, IEnumerable<string> keywords, string indent)
    {
        builder.Append(indent).AppendLine("<KeywordList>");
        foreach (var keyword in keywords.Where(keyword => !string.IsNullOrWhiteSpace(keyword)).Distinct(StringComparer.Ordinal))
        {
            builder.Append(indent).Append("  <Keyword>").Append(EscapeXml(keyword)).AppendLine("</Keyword>");
        }
        builder.Append(indent).AppendLine("</KeywordList>");
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
        if (SpatialReferenceHelpers.TryParseCrsDefinition(crs, out var definition) &&
            ResolveWmsBboxAxisOrder(isWms111 ? Wms111Version : Wms13Version, definition.AxisOrder) == AxisOrder.NorthEast)
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

    private readonly record struct Wgs84Extent(double MinLon, double MinLat, double MaxLon, double MaxLat);

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
