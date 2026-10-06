// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Microsoft.Extensions.Options;
using static Honua.Protocols.GeoServices.Soap.ArcGisSoapProtocol;

namespace Honua.Protocols.GeoServices.ImageServer;

internal static partial class ImageServerSoapEndpoints
{
    // Advertising stays opt-in (#1456). A service that does not advertise the cache still
    // answers GetCacheDescriptionInfo with nil properties; tile reads say the service is
    // not fixed-scale instead of pretending the operation is unknown.
    private const string NotFixedScaleMessage = "Image service is not a fixed-scale cache.";

    private static async Task<IResult> HandleGetCacheDescriptionInfoAsync(
        SoapRasterRequestContext request,
        CancellationToken cancellationToken)
    {
        var revalidation = await RevalidateMetadataAsync(request, cancellationToken).ConfigureAwait(false);
        if (revalidation.Error is not null)
        {
            return revalidation.Error;
        }

        var options = TileMetadata(request.HttpContext);
        XNamespace xsi = XmlSchemaInstanceNamespace;
        XElement body;
        if (!options.Enabled)
        {
            body = new XElement(
                "Result",
                new XAttribute(xsi + "type", "tns:CacheDescriptionInfo"),
                NilElement("TileCacheInfo"),
                NilElement("TileImageInfo"),
                NilElement("LayerCacheInfos"),
                NilElement("CacheControlInfo"),
                NilElement("ServiceType"));
        }
        else
        {
            var tileInfo = ImageServerTileInfoBuilder.Build(options.MaxLevel);
            body = new XElement(
                "Result",
                new XAttribute(xsi + "type", "tns:CacheDescriptionInfo"),
                BuildTileCacheElement("TileCacheInfo", tileInfo),
                BuildTileImageElement("TileImageInfo", tileInfo),
                new XElement("LayerCacheInfos", new XAttribute(xsi + "type", "tns:ArrayOfLayerCacheInfo")),
                new XElement(
                    "CacheControlInfo",
                    new XAttribute(xsi + "type", "tns:CacheControlInfo"),
                    new XElement("ClientCachingAllowed", true)),
                new XElement("ServiceType", "esriCachedMapServiceSingleFused"));
        }

        return CreateSoapResponse(
            request.SoapNamespace,
            request.OperationNamespace,
            "GetCacheDescriptionInfoResponse",
            body);
    }

    private static Task<IResult> HandleGetTileCacheInfoAsync(
        SoapRasterRequestContext request,
        CancellationToken cancellationToken)
        => HandleFixedScaleCacheAsync(
            request,
            "GetTileCacheInfoResponse",
            static tileInfo => BuildTileCacheElement("Result", tileInfo),
            cancellationToken);

    private static Task<IResult> HandleGetTileImageInfoAsync(
        SoapRasterRequestContext request,
        CancellationToken cancellationToken)
        => HandleFixedScaleCacheAsync(
            request,
            "GetTileImageInfoResponse",
            static tileInfo => BuildTileImageElement("Result", tileInfo),
            cancellationToken);

    private static async Task<IResult> HandleGetImageTileAsync(
        XElement operation,
        SoapRasterRequestContext request,
        CancellationToken cancellationToken)
    {
        if (!TryReadInt(operation, "Level", out var level)
            || !TryReadInt(operation, "Row", out var row)
            || !TryReadInt(operation, "Column", out var column))
        {
            return CreateSoapFault(
                "Level, Row, and Column are required.",
                StatusCodes.Status400BadRequest,
                request.SoapNamespace);
        }

        var formatElement = DirectChild(operation, "Format");
        string format;
        if (formatElement is null || string.IsNullOrWhiteSpace(formatElement.Value))
        {
            format = "png";
        }
        else if (MapImageFormat(formatElement.Value) is not { } mapped)
        {
            return CreateSoapFault(
                "Unsupported tile format.",
                StatusCodes.Status400BadRequest,
                request.SoapNamespace);
        }
        else
        {
            format = mapped;
        }

        if (!TileMetadata(request.HttpContext).Enabled)
        {
            return CreateSoapFault(
                NotFixedScaleMessage,
                StatusCodes.Status400BadRequest,
                request.SoapNamespace);
        }

        var revalidation = await RevalidateRasterPublicationAsync(
            request.Resolution,
            request.HttpContext,
            request.HttpContext.RequestServices.GetRequiredService<IImageServerLayerResolver>(),
            AuthorizationOperation.Export,
            request.SoapNamespace,
            cancellationToken).ConfigureAwait(false);
        if (revalidation.ErrorResult is not null)
        {
            return revalidation.ErrorResult;
        }

        var current = revalidation.Resolution;
        var handler = request.HttpContext.RequestServices.GetRequiredService<ImageServerTileHandler>();
        var result = await handler.GetImageTileAsync(
            request.HttpContext,
            current.LayerId,
            level,
            row,
            column,
            format,
            current.PublicationId,
            current.PublicationLayerIndex,
            cancellationToken).ConfigureAwait(false);
        if (result is Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult { FileContents: var imageData })
        {
            return CreateSoapResponse(
                request.SoapNamespace,
                request.OperationNamespace,
                "GetImageTileResponse",
                new XElement("Result", Convert.ToBase64String(imageData.Span)));
        }

        return CreateSoapFaultFromResult(result, "Image tile could not be read.", request.SoapNamespace);
    }

    private static async Task<IResult> HandleFixedScaleCacheAsync(
        SoapRasterRequestContext request,
        string responseName,
        Func<TileInfo, XElement> build,
        CancellationToken cancellationToken)
    {
        var revalidation = await RevalidateMetadataAsync(request, cancellationToken).ConfigureAwait(false);
        if (revalidation.Error is not null)
        {
            return revalidation.Error;
        }

        var options = TileMetadata(request.HttpContext);
        if (!options.Enabled)
        {
            return CreateSoapFault(
                NotFixedScaleMessage,
                StatusCodes.Status400BadRequest,
                request.SoapNamespace);
        }

        return CreateSoapResponse(
            request.SoapNamespace,
            request.OperationNamespace,
            responseName,
            build(ImageServerTileInfoBuilder.Build(options.MaxLevel)));
    }

    private static XElement BuildTileCacheElement(string name, TileInfo tileInfo)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        var wkid = tileInfo.SpatialReference.Wkid ?? 102100;
        var latestWkid = tileInfo.SpatialReference.LatestWkid ?? wkid;
        return new XElement(
            name,
            new XAttribute(xsi + "type", "tns:TileCacheInfo"),
            BuildProjectedSpatialReference(wkid, latestWkid),
            new XElement(
                "TileOrigin",
                new XAttribute(xsi + "type", "tns:PointN"),
                new XElement("X", FormatDouble(tileInfo.Origin.X)),
                new XElement("Y", FormatDouble(tileInfo.Origin.Y))),
            new XElement("TileCols", tileInfo.Cols),
            new XElement("TileRows", tileInfo.Rows),
            new XElement("DPI", tileInfo.Dpi),
            new XElement(
                "LODInfos",
                new XAttribute(xsi + "type", "tns:ArrayOfLODInfo"),
                tileInfo.Lods.Select(static lod => new XElement(
                    "LODInfo",
                    new XAttribute(XmlSchemaInstanceNamespace + "type", "tns:LODInfo"),
                    new XElement("LevelID", lod.Level),
                    new XElement("Resolution", FormatDouble(lod.Resolution)),
                    new XElement("Scale", FormatDouble(lod.Scale))))));
    }

    private static XElement BuildTileImageElement(string name, TileInfo tileInfo)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        return new XElement(
            name,
            new XAttribute(xsi + "type", "tns:TileImageInfo"),
            new XElement("CacheTileFormat", tileInfo.Format),
            new XElement("CompressionQuality", 0),
            new XElement("Antialiasing", true));
    }

    private static XElement BuildProjectedSpatialReference(int wkid, int latestWkid)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        return new XElement(
            "SpatialReference",
            new XAttribute(
                xsi + "type",
                GeographicSridClassifier.IsGeographicSrid(wkid)
                    ? "tns:GeographicCoordinateSystem"
                    : "tns:ProjectedCoordinateSystem"),
            new XElement("WKID", wkid),
            new XElement("LatestWKID", latestWkid));
    }

    private static ImageServerTileMetadataOptions TileMetadata(HttpContext context)
        => context.RequestServices.GetRequiredService<IOptions<ImageServerTileMetadataOptions>>().Value;
}
