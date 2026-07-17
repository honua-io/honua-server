// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Handler for the Image Server <c>kml/image.kmz</c> resource.
/// </summary>
/// <remarks>
/// Mirrors the ArcGIS ImageServer KML resource: it returns an OGC KML 2.2 KMZ
/// carrying a single <c>GroundOverlay</c> whose <c>Icon</c> references the service's
/// <c>exportImage</c> operation over the WGS84 service extent, with a
/// <c>LatLonBox</c> set to that extent. The overlay image is produced on demand by
/// the shared export pipeline when a KML client resolves the icon href, so this
/// resource itself renders no pixels and only reads catalog extent metadata.
/// </remarks>
internal sealed class ImageServerKmlHandler
{
    private const string KmzContentType = "application/vnd.google-earth.kmz";
    private const string KmlNamespace = "http://www.opengis.net/kml/2.2";
    private const string KmzEntryName = "doc.kml";
    private const int Wgs84Srid = 4326;

    /// <summary>Long-edge pixel budget for the referenced exportImage overlay.</summary>
    private const int OverlayLongEdgePixels = 1024;

    /// <summary>Upper bound on either overlay edge to keep the referenced export bounded.</summary>
    private const int MaxOverlayEdgePixels = 2048;

    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IRasterStore _rasterStore;
    private readonly ImageServerCoordinateProjection _projection;
    private readonly ILogger<ImageServerKmlHandler> _logger;

    public ImageServerKmlHandler(
        IMetadataV2GraphProvider graphProvider,
        IRasterStore rasterStore,
        ImageServerCoordinateProjection projection,
        ILogger<ImageServerKmlHandler> logger)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Builds the Image Server <c>image.kmz</c> KMZ for the resolved layer.
    /// </summary>
    public async Task<IResult> GetKmlAsync(
        HttpContext context,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is not { } resolved)
        {
            ImageServerLog.LayerNotFound(_logger, layerId);
            return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
        }

        var rasters = await _rasterStore.ListRastersAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (rasters.Length == 0)
        {
            ImageServerLog.NoRastersFound(_logger, layerId);
            return StandardErrorHelpers.CreateNotFound(context, "No rasters found for layer.");
        }

        if (ImageServerMosaicHelpers.ComputeAggregateExtent(rasters) is not { } extent)
        {
            ImageServerLog.ExtentNotAvailable(_logger, layerId);
            return StandardErrorHelpers.CreateInternalServerError(context, "Unable to determine raster extent.");
        }

        var wgs84 = await ProjectToWgs84Async(extent, cancellationToken).ConfigureAwait(false);
        if (wgs84 is not { } box)
        {
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "Unable to project raster extent to WGS84 for KML.");
        }

        var serviceName = string.IsNullOrWhiteSpace(resolved.DisplayName) ? "ImageServer" : resolved.DisplayName;
        var overlayHref = BuildExportImageHref(context, box);
        var kmz = BuildKmz(serviceName, resolved.Description, box, overlayHref);
        return Results.Bytes(kmz, KmzContentType, fileDownloadName: "image.kmz");
    }

    /// <summary>
    /// Projects the aggregate extent into WGS84 lat/lon. Native geographic extents pass
    /// through unchanged; other spatial references require the shared transform service.
    /// </summary>
    private async ValueTask<GeographicBox?> ProjectToWgs84Async(
        RasterExtent extent,
        CancellationToken cancellationToken)
    {
        var srid = extent.Srid ?? Wgs84Srid;
        if (srid == Wgs84Srid)
        {
            return new GeographicBox(extent.XMin, extent.YMin, extent.XMax, extent.YMax);
        }

        if (!_projection.HasTransformService)
        {
            return null;
        }

        var transformed = await _projection
            .TransformExtentAsync(extent.XMin, extent.YMin, extent.XMax, extent.YMax, srid, Wgs84Srid, cancellationToken)
            .ConfigureAwait(false);
        if (transformed is not { } t)
        {
            return null;
        }

        return new GeographicBox(t.MinX, t.MinY, t.MaxX, t.MaxY);
    }

    /// <summary>
    /// Builds a self-referential absolute exportImage URL for the overlay icon,
    /// preserving whichever service addressing form (numeric-layer or service-name)
    /// the KML request used.
    /// </summary>
    private static string BuildExportImageHref(HttpContext context, GeographicBox box)
    {
        var pathBase = context.Request.PathBase.HasValue ? context.Request.PathBase.Value! : string.Empty;
        var path = context.Request.Path.Value ?? string.Empty;
        var kmlIndex = path.LastIndexOf("/kml/", StringComparison.OrdinalIgnoreCase);
        var servicePath = kmlIndex >= 0 ? path[..kmlIndex] : path;

        var (width, height) = ComputeOverlaySize(box);
        var query = string.Create(
            CultureInfo.InvariantCulture,
            $"?bbox={box.West},{box.South},{box.East},{box.North}&bboxSR={Wgs84Srid}&imageSR={Wgs84Srid}&size={width},{height}&format=png&transparent=true&f=image");

        var host = context.Request.Host.HasValue ? context.Request.Host.Value : "localhost";
        return $"{context.Request.Scheme}://{host}{pathBase}{servicePath}/exportImage{query}";
    }

    /// <summary>Sizes the overlay so its long edge is <see cref="OverlayLongEdgePixels"/>, aspect-preserved.</summary>
    private static (int Width, int Height) ComputeOverlaySize(GeographicBox box)
    {
        var spanX = Math.Abs(box.East - box.West);
        var spanY = Math.Abs(box.North - box.South);
        if (spanX <= 0 || spanY <= 0)
        {
            return (OverlayLongEdgePixels, OverlayLongEdgePixels);
        }

        int width;
        int height;
        if (spanX >= spanY)
        {
            width = OverlayLongEdgePixels;
            height = (int)Math.Round(OverlayLongEdgePixels * (spanY / spanX), MidpointRounding.AwayFromZero);
        }
        else
        {
            height = OverlayLongEdgePixels;
            width = (int)Math.Round(OverlayLongEdgePixels * (spanX / spanY), MidpointRounding.AwayFromZero);
        }

        return (Math.Clamp(width, 1, MaxOverlayEdgePixels), Math.Clamp(height, 1, MaxOverlayEdgePixels));
    }

    private static byte[] BuildKmz(string serviceName, string? description, GeographicBox box, string overlayHref)
    {
        var kml = BuildKmlDocument(serviceName, description, box, overlayHref);

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(KmzEntryName, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            entryStream.Write(kml);
        }

        return buffer.ToArray();
    }

    private static byte[] BuildKmlDocument(string serviceName, string? description, GeographicBox box, string overlayHref)
    {
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        using (var writer = XmlWriter.Create(stream, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("kml", KmlNamespace);
            writer.WriteStartElement("Document");

            writer.WriteElementString("name", serviceName);
            if (!string.IsNullOrWhiteSpace(description))
            {
                writer.WriteElementString("description", description);
            }

            writer.WriteElementString("open", "1");

            writer.WriteStartElement("GroundOverlay");
            writer.WriteElementString("name", serviceName);

            writer.WriteStartElement("Icon");
            writer.WriteElementString("href", overlayHref);
            writer.WriteElementString("viewBoundScale", "0.75");
            writer.WriteEndElement(); // Icon

            writer.WriteStartElement("LatLonBox");
            writer.WriteElementString("north", FormatCoordinate(box.North));
            writer.WriteElementString("south", FormatCoordinate(box.South));
            writer.WriteElementString("east", FormatCoordinate(box.East));
            writer.WriteElementString("west", FormatCoordinate(box.West));
            writer.WriteEndElement(); // LatLonBox

            writer.WriteEndElement(); // GroundOverlay
            writer.WriteEndElement(); // Document
            writer.WriteEndElement(); // kml
            writer.WriteEndDocument();
        }

        return stream.ToArray();
    }

    private static string FormatCoordinate(double value)
        => value.ToString("0.##########", CultureInfo.InvariantCulture);

    private readonly record struct GeographicBox(double West, double South, double East, double North);
}
