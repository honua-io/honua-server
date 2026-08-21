// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Helpers;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Microsoft.AspNetCore.Mvc;
using SkiaSharp;

namespace Honua.Protocols.GeoServices.ImageServer;

/// <summary>
/// Bounded ArcGIS SOAP adapter for the read-only ImageServer operations used by
/// ArcGIS Pro. Rendering delegates to the canonical REST ImageServer handler so
/// the SOAP surface never becomes a second raster execution engine.
/// </summary>
internal static class ImageServerSoapEndpoints
{
    private const string Soap11ContentType = "text/xml; charset=utf-8";
    private const string Soap12ContentType = "application/soap+xml; charset=utf-8";
    private const string Soap11EnvelopeNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string Soap12EnvelopeNamespace = "http://www.w3.org/2003/05/soap-envelope";
    private const string ArcGisSoapNamespace = "http://www.esri.com/schemas/ArcGIS/10.8";
    private const string XmlSchemaNamespace = "http://www.w3.org/2001/XMLSchema";
    private const string XmlSchemaInstanceNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    private const int MaxRequestCharacters = 1_048_576;
    private const int MaxImageDimension = 4096;

    public static void MapImageServerSoapEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/services/{serviceId}/ImageServer", HandlePostSoapImageServer)
            .WithDisplayName("ArcGIS SOAP ImageServer")
            .WithName("ArcGisSoapImageServer")
            .WithSummary("Read and render an ImageServer through ArcGIS SOAP")
            .WithDescription("Implements the bounded ArcGIS Pro ImageServer SOAP compatibility surface over Honua's canonical raster service.")
            .WithTags("ImageServer")
            .Accepts<string>("text/xml", "application/soap+xml")
            .Produces(StatusCodes.Status200OK, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status400BadRequest, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status404NotFound, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status401Unauthorized, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status403Forbidden, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status500InternalServerError, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status501NotImplemented, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status503ServiceUnavailable, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .AllowAnonymous();
    }

    private static async Task<IResult> HandlePostSoapImageServer(
        string serviceId,
        HttpContext context,
        [FromServices] IImageServerLayerResolver layerResolver,
        [FromServices] IRasterStore rasterStore,
        [FromServices] ImageServerExportHandler exportHandler,
        [FromServices] ILogger<ImageServerSoapLog> logger)
    {
        var request = await TryReadSoapRequestAsync(context).ConfigureAwait(false);
        if (request.ErrorResult is not null)
        {
            return request.ErrorResult;
        }

        var operation = request.Operation!;
        var soapNamespace = request.SoapNamespace!;
        var operationNamespace = operation.Name.Namespace;
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        try
        {
            var resolution = await layerResolver.ResolveFirstAccessibleLayerAsync(
                serviceId,
                context,
                cancellationToken).ConfigureAwait(false);
            if (resolution.ErrorResult is not null)
            {
                var statusCode = (resolution.ErrorResult as IStatusCodeHttpResult)?.StatusCode
                    ?? StatusCodes.Status404NotFound;
                return CreateSoapFault("Image service was not found or is not accessible.", statusCode, soapNamespace);
            }

            return operation.Name.LocalName switch
            {
                "GetVersion" => CreateSoapResponse(
                    soapNamespace,
                    operationNamespace,
                    "GetVersionResponse",
                    new XElement("Result", "10.8")),
                "IsFixedScaleImage" => CreateSoapResponse(
                    soapNamespace,
                    operationNamespace,
                    "IsFixedScaleImageResponse",
                    new XElement("Result", false)),
                "GetServiceInfo" => await HandleGetServiceInfoAsync(
                    serviceId,
                    resolution.LayerId,
                    soapNamespace,
                    operationNamespace,
                    rasterStore,
                    cancellationToken).ConfigureAwait(false),
                "GetFields" => CreateSoapResponse(
                    soapNamespace,
                    operationNamespace,
                    "GetFieldsResponse",
                    BuildFields()),
                "GetKeyProperties" => CreateSoapResponse(
                    soapNamespace,
                    operationNamespace,
                    "GetKeyPropertiesResponse",
                    BuildKeyProperties()),
                "GetMetadata" => CreateSoapResponse(
                    soapNamespace,
                    operationNamespace,
                    "GetMetadataResponse",
                    new XElement("Result", BuildMetadata(serviceId))),
                "ExportImage" => await HandleExportImageAsync(
                    operation,
                    soapNamespace,
                    operationNamespace,
                    resolution.PublicationLayerIndex ?? resolution.LayerId,
                    resolution.PublicationId!,
                    context,
                    exportHandler,
                    cancellationToken).ConfigureAwait(false),
                "GetImage" => await HandleGetImageAsync(
                    operation,
                    soapNamespace,
                    operationNamespace,
                    resolution.PublicationLayerIndex ?? resolution.LayerId,
                    resolution.PublicationId!,
                    resolution.LayerId,
                    context,
                    rasterStore,
                    exportHandler,
                    cancellationToken).ConfigureAwait(false),
                _ => CreateSoapFault(
                    $"Unsupported ImageServer operation '{operation.Name.LocalName}'.",
                    StatusCodes.Status400BadRequest,
                    soapNamespace)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ImageServerSoapEndpointLogging.LogOperationFailed(
                logger,
                serviceId,
                operation.Name.LocalName,
                exception);
            return CreateSoapFault(
                "The ImageServer operation could not be completed.",
                StatusCodes.Status500InternalServerError,
                soapNamespace);
        }
    }

    private static async Task<IResult> HandleGetServiceInfoAsync(
        string serviceId,
        int layerId,
        XNamespace soapNamespace,
        XNamespace operationNamespace,
        IRasterStore rasterStore,
        CancellationToken cancellationToken)
    {
        var rasters = await rasterStore.ListRastersAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (rasters.Length == 0)
        {
            return CreateSoapFault("Image service has no rasters.", StatusCodes.Status404NotFound, soapNamespace);
        }

        var extent = ImageServerMosaicHelpers.ComputeAggregateExtent(rasters);
        if (extent is null)
        {
            return CreateSoapFault("Image service extent is unavailable.", StatusCodes.Status500InternalServerError, soapNamespace);
        }

        var referenceRaster = rasters.FirstOrDefault(static raster => raster.Extent.HasValue);
        if (referenceRaster.Width <= 0 || referenceRaster.Height <= 0)
        {
            referenceRaster = rasters[0];
        }

        var referenceExtent = referenceRaster.Extent;
        var pixelSizeX = referenceRaster.Width > 0 && referenceExtent.HasValue
            ? (referenceExtent.Value.XMax - referenceExtent.Value.XMin) / referenceRaster.Width
            : 0;
        var pixelSizeY = referenceRaster.Height > 0 && referenceExtent.HasValue
            ? (referenceExtent.Value.YMax - referenceExtent.Value.YMin) / referenceRaster.Height
            : 0;
        XNamespace xsi = XmlSchemaInstanceNamespace;

        var result = new XElement(
            "Result",
            new XAttribute(xsi + "type", "tns:ImageServiceInfo"),
            new XElement("Name", serviceId),
            new XElement("Description", $"Image service for {serviceId}"),
            BuildEnvelope(extent.Value),
            new XElement("PixelSizeX", FormatDouble(pixelSizeX)),
            new XElement("PixelSizeY", FormatDouble(pixelSizeY)),
            new XElement("BandCount", referenceRaster.BandCount),
            new XElement("PixelType", MapPixelType(referenceRaster.PixelType)),
            BuildNoData(referenceRaster.NoDataValue),
            new XElement("MinPixelSize", "0"),
            new XElement("MaxPixelSize", "0"),
            new XElement("CopyrightText", string.Empty),
            new XElement("ServiceDataType", "esriImageServiceDataTypeGeneric"),
            new XElement("ServiceProperties", string.Empty),
            new XElement("MaxNCols", MaxImageDimension),
            new XElement("MaxNRows", MaxImageDimension),
            new XElement("ServiceSourceType", "esriImageServiceSourceTypeMosaicDataset"),
            new XElement("AllowedFields", "OBJECTID,Shape,Name"),
            new XElement("AllowedCompressions", "None"),
            new XElement("AllowedMosaicMethods", "NorthWest,Center,LockRaster,None"),
            new XElement("MaxRecordCount", 1000),
            new XElement("MaxMosaicImageCount", 20),
            new XElement("DefaultCompression", "None"),
            new XElement("DefaultCompressionQuality", 75),
            new XElement("DefaultResamplingMethod", "RSP_BilinearInterpolation"),
            new XElement("DefaultMosaicMethod", "esriMosaicNorthwest"),
            new XElement("SupportBSQ", false),
            new XElement("SupportsTime", false),
            new XElement("MensurationCapabilities", "Basic"),
            new XElement("HasRasterAttributeTable", false),
            new XElement("MinScale", 0),
            new XElement("MaxScale", 0));

        return CreateSoapResponse(soapNamespace, operationNamespace, "GetServiceInfoResponse", result);
    }

    private static async Task<IResult> HandleExportImageAsync(
        XElement operation,
        XNamespace soapNamespace,
        XNamespace operationNamespace,
        int publicationLayerIndex,
        string publicationId,
        HttpContext context,
        ImageServerExportHandler exportHandler,
        CancellationToken cancellationToken)
    {
        if (!TryCreateExportRequest(operation, out var request, out var width, out var height, out var error))
        {
            return CreateSoapFault(error!, StatusCodes.Status400BadRequest, soapNamespace);
        }

        var returnType = FindDescendantValue(operation, "ImageReturnType");
        var returnMimeData = string.Equals(returnType, "esriImageReturnMimeData", StringComparison.Ordinal);
        request = CopyWithResponseFormat(request, returnMimeData ? "image" : "json");
        var exportResult = await exportHandler.ExportImageAsync(
            context,
            publicationLayerIndex,
            request,
            publicationId,
            cancellationToken).ConfigureAwait(false);

        if (returnMimeData && exportResult is Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult
            { FileContents: var imageData })
        {
            XNamespace xsi = XmlSchemaInstanceNamespace;
            var mimeResult = new XElement(
                "Result",
                new XAttribute(xsi + "type", "tns:ImageResult"),
                new XElement("ImageData", Convert.ToBase64String(imageData.Span)),
                new XElement("ImageURL", string.Empty),
                new XElement("ImageHeight", height),
                new XElement("ImageWidth", width),
                new XElement("ImageDPI", 0),
                new XElement("ImageType", MapMimeType(request.Format)));
            return CreateSoapResponse(soapNamespace, operationNamespace, "ExportImageResponse", mimeResult);
        }

        if (!returnMimeData && exportResult is IValueHttpResult { Value: ExportImageResponse response })
        {
            XNamespace xsi = XmlSchemaInstanceNamespace;
            var imageUrl = Uri.TryCreate(response.Href, UriKind.Absolute, out _)
                ? response.Href
                : $"{BaseUrlResolver.GetBaseUrl(context)}{(response.Href.StartsWith('/') ? string.Empty : "/")}{response.Href}";
            var urlResult = new XElement(
                "Result",
                new XAttribute(xsi + "type", "tns:ImageResult"),
                new XElement("ImageURL", imageUrl),
                new XElement("ImageHeight", response.Height),
                new XElement("ImageWidth", response.Width),
                new XElement("ImageDPI", 0),
                new XElement("ImageType", MapMimeType(request.Format)));
            return CreateSoapResponse(soapNamespace, operationNamespace, "ExportImageResponse", urlResult);
        }

        return CreateSoapFaultFromResult(exportResult, "Image export failed.", soapNamespace);
    }

    private static async Task<IResult> HandleGetImageAsync(
        XElement operation,
        XNamespace soapNamespace,
        XNamespace operationNamespace,
        int publicationLayerIndex,
        string publicationId,
        int storageLayerId,
        HttpContext context,
        IRasterStore rasterStore,
        ImageServerExportHandler exportHandler,
        CancellationToken cancellationToken)
    {
        if (!TryCreateExportRequest(operation, out var request, out var width, out var height, out var error, requireImageType: false))
        {
            return CreateSoapFault(error!, StatusCodes.Status400BadRequest, soapNamespace);
        }

        var rasters = await rasterStore.ListRastersAsync(storageLayerId, cancellationToken).ConfigureAwait(false);
        var referenceRaster = rasters.FirstOrDefault(static raster => raster.Extent.HasValue);
        if (referenceRaster.Id == 0 && rasters.Length > 0)
        {
            referenceRaster = rasters[0];
        }

        if (referenceRaster.Id == 0)
        {
            return CreateSoapFault("Image service has no rasters.", StatusCodes.Status404NotFound, soapNamespace);
        }

        if (!string.Equals(referenceRaster.PixelType, "8BUI", StringComparison.OrdinalIgnoreCase))
        {
            return CreateSoapFault(
                "SOAP GetImage currently supports unsigned 8-bit image services.",
                StatusCodes.Status400BadRequest,
                soapNamespace);
        }

        if (request.Compression is not null
            && !string.Equals(request.Compression, "None", StringComparison.OrdinalIgnoreCase))
        {
            return CreateSoapFault(
                "SOAP GetImage supports uncompressed Esri pixel blocks only.",
                StatusCodes.Status400BadRequest,
                soapNamespace);
        }

        var bandCount = ResolveGetImageBandCount(request.BandIds, referenceRaster.BandCount);
        if (bandCount is not (1 or 3))
        {
            return CreateSoapFault(
                "SOAP GetImage currently supports one-band grayscale or three-band RGB pixel blocks; " +
                "multispectral bands require a raw-sample renderer that preserves a distinct NoData mask.",
                StatusCodes.Status501NotImplemented,
                soapNamespace);
        }

        request = CopyWithResponseFormat(request, "image");
        var exportResult = await exportHandler.ExportImageAsync(
            context,
            publicationLayerIndex,
            request,
            publicationId,
            cancellationToken).ConfigureAwait(false);
        if (exportResult is Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult
            { FileContents: var imageData })
        {
            if (!TryBuildEsriPixelBlock(imageData.Span, width, height, bandCount, out var pixelBlock))
            {
                return CreateSoapFault(
                    "The canonical raster renderer returned an invalid image payload.",
                    StatusCodes.Status500InternalServerError,
                    soapNamespace);
            }

            return CreateSoapResponse(
                soapNamespace,
                operationNamespace,
                "GetImageResponse",
                new XElement("Result", Convert.ToBase64String(pixelBlock)));
        }

        return CreateSoapFaultFromResult(exportResult, "Image export failed.", soapNamespace);
    }

    /// <summary>
    /// Converts the canonical encoded render into Esri GetImage binary layout: unsigned
    /// 8-bit samples in band-interleaved-by-pixel order followed by a packed validity
    /// mask. Mask bits are stored most-significant-bit first; one means valid and zero
    /// means NoData, with no row padding.
    /// </summary>
    private static bool TryBuildEsriPixelBlock(
        ReadOnlySpan<byte> encodedImage,
        int expectedWidth,
        int expectedHeight,
        int bandCount,
        out byte[] pixelBlock)
    {
        pixelBlock = [];
        using var encoded = SKData.CreateCopy(encodedImage);
        using var bitmap = SKBitmap.Decode(encoded);
        if (bitmap is null || bitmap.Width != expectedWidth || bitmap.Height != expectedHeight)
        {
            return false;
        }

        var pixelCount = checked(expectedWidth * expectedHeight);
        var sampleByteCount = checked(pixelCount * bandCount);
        var maskByteCount = checked((pixelCount + 7) / 8);
        pixelBlock = GC.AllocateUninitializedArray<byte>(checked(sampleByteCount + maskByteCount));
        pixelBlock.AsSpan(sampleByteCount, maskByteCount).Clear();

        var sampleOffset = 0;
        for (var y = 0; y < expectedHeight; y++)
        {
            for (var x = 0; x < expectedWidth; x++)
            {
                var color = bitmap.GetPixel(x, y);
                pixelBlock[sampleOffset++] = color.Red;
                if (bandCount >= 2)
                {
                    pixelBlock[sampleOffset++] = color.Green;
                }

                if (bandCount >= 3)
                {
                    pixelBlock[sampleOffset++] = color.Blue;
                }

                if (bandCount >= 4)
                {
                    pixelBlock[sampleOffset++] = color.Alpha;
                }

                if (color.Alpha != 0)
                {
                    var pixelIndex = (y * expectedWidth) + x;
                    pixelBlock[sampleByteCount + (pixelIndex / 8)] |= (byte)(0x80 >> (pixelIndex % 8));
                }
            }
        }

        return true;
    }

    private static int ResolveGetImageBandCount(string? bandIds, int serviceBandCount)
    {
        if (!string.IsNullOrWhiteSpace(bandIds))
        {
            return bandIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        }

        return serviceBandCount;
    }

    private static bool TryCreateExportRequest(
        XElement operation,
        out ExportImageRequest request,
        out int width,
        out int height,
        out string? error,
        bool requireImageType = true)
    {
        request = new ExportImageRequest();
        width = 0;
        height = 0;
        error = null;

        var description = operation.Elements().FirstOrDefault(static element => element.Name.LocalName == "ImageDescription");
        if (description is null)
        {
            error = "ImageDescription is required.";
            return false;
        }

        var extent = description.Descendants().FirstOrDefault(static element => element.Name.LocalName == "Extent");
        if (extent is null ||
            !TryReadFiniteDouble(extent, "XMin", out var xMin) ||
            !TryReadFiniteDouble(extent, "YMin", out var yMin) ||
            !TryReadFiniteDouble(extent, "XMax", out var xMax) ||
            !TryReadFiniteDouble(extent, "YMax", out var yMax) ||
            xMin >= xMax || yMin >= yMax)
        {
            error = "ImageDescription extent must be a finite, non-empty envelope.";
            return false;
        }

        if (!TryReadInt(description, "Width", out width) ||
            !TryReadInt(description, "Height", out height) ||
            width is < 1 or > MaxImageDimension ||
            height is < 1 or > MaxImageDimension)
        {
            error = $"ImageDescription width and height must be between 1 and {MaxImageDimension}.";
            return false;
        }

        var imageType = operation.Elements().FirstOrDefault(static element => element.Name.LocalName == "ImageType");
        if (requireImageType && imageType is null)
        {
            error = "ImageType is required.";
            return false;
        }

        var format = MapImageFormat(FindDescendantValue(imageType, "ImageFormat"));
        if (format is null)
        {
            error = "ImageFormat must be PNG, JPG, or TIFF.";
            return false;
        }

        var spatialReference = extent.Elements()
            .FirstOrDefault(static element => element.Name.LocalName == "SpatialReference");
        var wkid = FindDescendantValue(spatialReference, "LatestWKID")
            ?? FindDescendantValue(spatialReference, "WKID");

        request = new ExportImageRequest
        {
            Bbox = FormattableString.Invariant($"{xMin},{yMin},{xMax},{yMax}"),
            Size = FormattableString.Invariant($"{width},{height}"),
            BboxSr = wkid,
            ImageSr = wkid,
            Format = format,
            PixelType = NormalizeOptionalValue(FindDescendantValue(description, "PixelType")),
            NoData = NormalizeOptionalValue(FindDescendantValue(description, "NoData")),
            Interpolation = NormalizeOptionalValue(FindDescendantValue(description, "Interpolation")),
            Compression = NormalizeOptionalValue(FindDescendantValue(description, "Compression")),
            CompressionQuality = TryReadInt(description, "CompressionQuality", out var quality) ? quality : 75,
            BandIds = string.Join(",", description.Descendants()
                .Where(static element => element.Name.LocalName == "Int")
                .Select(static element => element.Value)),
            F = "json"
        };
        return true;
    }

    private static ExportImageRequest CopyWithResponseFormat(ExportImageRequest request, string responseFormat)
        => new()
        {
            Bbox = request.Bbox,
            Size = request.Size,
            ImageSr = request.ImageSr,
            BboxSr = request.BboxSr,
            Format = request.Format,
            PixelType = request.PixelType,
            NoData = request.NoData,
            Interpolation = request.Interpolation,
            Compression = request.Compression,
            CompressionQuality = request.CompressionQuality,
            BandIds = string.IsNullOrWhiteSpace(request.BandIds) ? null : request.BandIds,
            F = responseFormat
        };

    private static XElement BuildFields()
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        return new XElement(
            "Result",
            new XAttribute(xsi + "type", "tns:Fields"),
            new XElement(
                "FieldArray",
                new XAttribute(xsi + "type", "tns:ArrayOfField"),
                BuildField("OBJECTID", "esriFieldTypeOID", false, 4, required: true),
                BuildField("Shape", "esriFieldTypeGeometry", true, 0, required: true),
                BuildField("Name", "esriFieldTypeString", true, 255, required: false)));
    }

    private static XElement BuildField(
        string name,
        string type,
        bool nullable,
        int length,
        bool required)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        var field = new XElement(
            "Field",
            new XAttribute(xsi + "type", "tns:Field"),
            new XElement("Name", name),
            new XElement("Type", type),
            new XElement("IsNullable", nullable),
            new XElement("Length", length),
            new XElement("Precision", 0),
            new XElement("Scale", 0),
            new XElement("Required", required),
            new XElement("Editable", false));

        if (string.Equals(type, "esriFieldTypeGeometry", StringComparison.Ordinal))
        {
            field.Add(
                new XElement(
                    "GeometryDef",
                    new XAttribute(xsi + "type", "tns:GeometryDef"),
                    new XElement("AvgNumPoints", 0),
                    new XElement("GeometryType", "esriGeometryPolygon"),
                    new XElement("HasM", false),
                    new XElement("HasZ", false)));
        }

        field.Add(new XElement("AliasName", name), new XElement("ModelName", name));
        return field;
    }

    private static XElement BuildKeyProperties()
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        return new XElement(
            "Result",
            new XAttribute(xsi + "type", "tns:PropertySet"),
            new XElement(
                "PropertyArray",
                new XAttribute(xsi + "type", "tns:ArrayOfPropertySetProperty"),
                BuildProperty("BandDefinitionKeyword", "NONE", "xsd:string", xsi),
                BuildProperty("LowCellSize", "0", "xsd:double", xsi),
                BuildProperty("HighCellSize", "0", "xsd:double", xsi)));
    }

    private static XElement BuildProperty(string key, string value, string type, XNamespace xsi)
        => new(
            "PropertySetProperty",
            new XAttribute(xsi + "type", "tns:PropertySetProperty"),
            new XElement("Key", key),
            new XElement("Value", new XAttribute(xsi + "type", type), value));

    private static XElement BuildEnvelope(RasterExtent extent)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        return new XElement(
            "Extent",
            new XAttribute(xsi + "type", "tns:EnvelopeN"),
            new XElement("XMin", FormatDouble(extent.XMin)),
            new XElement("YMin", FormatDouble(extent.YMin)),
            new XElement("XMax", FormatDouble(extent.XMax)),
            new XElement("YMax", FormatDouble(extent.YMax)),
            new XElement(
                "SpatialReference",
                new XAttribute(
                    xsi + "type",
                    extent.Srid == 4326 ? "tns:GeographicCoordinateSystem" : "tns:ProjectedCoordinateSystem"),
                new XElement("WKID", extent.Srid ?? 0),
                new XElement("LatestWKID", extent.Srid ?? 0)));
    }

    private static XElement BuildNoData(double? noData)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        if (!noData.HasValue)
        {
            return new XElement("NoData", new XAttribute(xsi + "nil", true));
        }

        return new XElement(
            "NoData",
            new XAttribute(xsi + "type", "tns:ArrayOfAnyType"),
            new XElement("AnyType", new XAttribute(xsi + "type", "xsd:double"), FormatDouble(noData.Value)));
    }

    private static async Task<(XElement? Operation, XNamespace? SoapNamespace, IResult? ErrorResult)> TryReadSoapRequestAsync(
        HttpContext context)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxRequestCharacters
            };
            using var reader = XmlReader.Create(context.Request.Body, settings);
            var request = await XDocument.LoadAsync(reader, LoadOptions.None, context.RequestAborted).ConfigureAwait(false);
            var envelopeNamespace = request.Root?.Name.Namespace;
            if (request.Root?.Name.LocalName != "Envelope" ||
                (envelopeNamespace != Soap11EnvelopeNamespace && envelopeNamespace != Soap12EnvelopeNamespace))
            {
                var requestedSoap = RequestedSoapNamespace(context.Request);
                return (null, requestedSoap, CreateSoapFault(
                    "Unsupported SOAP envelope namespace.",
                    StatusCodes.Status400BadRequest,
                    requestedSoap));
            }

            XNamespace soap = envelopeNamespace;
            var bodies = request.Root.Elements(soap + "Body").Take(2).ToArray();
            if (bodies.Length != 1)
            {
                return (null, soap, CreateSoapFault(
                    "SOAP envelope must contain exactly one Body element.",
                    StatusCodes.Status400BadRequest,
                    soap));
            }

            var operations = bodies[0].Elements().Take(2).ToArray();
            if (operations is not { Length: 1 })
            {
                return (null, soap, CreateSoapFault(
                    "SOAP body must contain exactly one ImageServer operation.",
                    StatusCodes.Status400BadRequest,
                    soap));
            }

            if (operations[0].Name.Namespace != ArcGisSoapNamespace)
            {
                return (null, soap, CreateSoapFault(
                    "Unsupported ArcGIS SOAP operation namespace.",
                    StatusCodes.Status400BadRequest,
                    soap));
            }

            return (operations[0], soap, null);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            var requestedSoap = RequestedSoapNamespace(context.Request);
            return (null, requestedSoap, CreateSoapFault(
                "Malformed SOAP request.",
                StatusCodes.Status400BadRequest,
                requestedSoap));
        }
    }

    private static IResult CreateSoapResponse(
        XNamespace soap,
        XNamespace operationNamespace,
        string responseName,
        XElement result)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        XNamespace xsd = XmlSchemaNamespace;
        foreach (var element in result.DescendantsAndSelf()
                     .Where(static element => element.Name.Namespace == XNamespace.None))
        {
            element.Name = operationNamespace + element.Name.LocalName;
        }

        var response = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soap", soap.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "xsd", xsd.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "tns", operationNamespace.NamespaceName),
                new XElement(
                    soap + "Body",
                    new XElement(operationNamespace + responseName, result))));

        return Results.Content(
            response.ToString(SaveOptions.DisableFormatting),
            contentType: SoapContentTypeFor(soap),
            contentEncoding: Encoding.UTF8);
    }

    private static IResult CreateSoapFaultFromResult(IResult result, string message, XNamespace soap)
    {
        var statusCode = (result as IStatusCodeHttpResult)?.StatusCode
            ?? StatusCodes.Status500InternalServerError;
        return CreateSoapFault(message, statusCode, soap);
    }

    private static IResult CreateSoapFault(string message, int statusCode, XNamespace soap)
    {
        var fault = soap == Soap12EnvelopeNamespace
            ? new XElement(
                soap + "Fault",
                new XElement(
                    soap + "Code",
                    new XElement(soap + "Value", statusCode >= 500 ? "soap:Receiver" : "soap:Sender")),
                new XElement(
                    soap + "Reason",
                    new XElement(
                        soap + "Text",
                        new XAttribute(XNamespace.Xml + "lang", "en"),
                        message)))
            : new XElement(
                soap + "Fault",
                new XElement("faultcode", statusCode >= 500 ? "soap:Server" : "soap:Client"),
                new XElement("faultstring", message));
        var response = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soap", soap.NamespaceName),
                new XElement(
                    soap + "Body",
                    fault)));

        return Results.Content(
            response.ToString(SaveOptions.DisableFormatting),
            contentType: SoapContentTypeFor(soap),
            contentEncoding: Encoding.UTF8,
            statusCode: statusCode);
    }

    private static XNamespace RequestedSoapNamespace(HttpRequest request)
        => request.ContentType?.StartsWith("application/soap+xml", StringComparison.OrdinalIgnoreCase) == true
            ? Soap12EnvelopeNamespace
            : Soap11EnvelopeNamespace;

    private static string SoapContentTypeFor(XNamespace soap)
        => soap == Soap12EnvelopeNamespace ? Soap12ContentType : Soap11ContentType;

    private static string? FindDescendantValue(XElement? element, string localName)
        => element?.DescendantsAndSelf()
            .FirstOrDefault(candidate => candidate.Name.LocalName == localName)?.Value;

    private static bool TryReadFiniteDouble(XElement parent, string localName, out double value)
        => double.TryParse(
               FindDescendantValue(parent, localName),
               NumberStyles.Float,
               CultureInfo.InvariantCulture,
               out value)
           && double.IsFinite(value);

    private static bool TryReadInt(XElement parent, string localName, out int value)
        => int.TryParse(
            FindDescendantValue(parent, localName),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value);

    private static string? NormalizeOptionalValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? MapImageFormat(string? format)
        => format switch
        {
            null or "" or "esriImagePNG" or "esriImagePNG24" or "esriImagePNG32" => "png",
            "esriImageJPG" => "jpg",
            "esriImageTIFF" => "tiff",
            _ => null
        };

    private static string MapMimeType(string? format)
        => format switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "tif" or "tiff" => "image/tiff",
            _ => "image/png"
        };

    private static string MapPixelType(string postgisPixelType)
        => postgisPixelType.ToUpperInvariant() switch
        {
            "8BUI" => "U8",
            "8BSI" => "S8",
            "16BUI" => "U16",
            "16BSI" => "S16",
            "32BUI" => "U32",
            "32BSI" => "S32",
            "32BF" => "F32",
            "64BF" => "F64",
            _ => "U8"
        };

    private static string FormatDouble(double value)
        => value.ToString("R", CultureInfo.InvariantCulture);

    private static string BuildMetadata(string serviceId)
        => new XElement(
            "metadata",
            new XElement(
                "Esri",
                new XElement("ArcGISFormat", "1.0"),
                new XElement(
                    "DataProperties",
                    new XElement(
                        "itemProps",
                        new XElement("itemName", serviceId)))))
            .ToString(SaveOptions.DisableFormatting);
}

internal sealed class ImageServerSoapLog;

internal static partial class ImageServerSoapEndpointLogging
{
    [LoggerMessage(
        EventId = 9480,
        Level = LogLevel.Error,
        Message = "ArcGIS SOAP ImageServer operation failed for service {ServiceId}: {Operation}")]
    internal static partial void LogOperationFailed(
        ILogger logger,
        string serviceId,
        string operation,
        Exception exception);
}
