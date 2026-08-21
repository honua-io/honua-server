// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Helpers;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.Protocols.GeoServices.Soap;
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
    private const string SoapContentType = "text/xml; charset=utf-8";
    private const string XmlSchemaNamespace = "http://www.w3.org/2001/XMLSchema";
    private const string XmlSchemaInstanceNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    private const int MaxImageDimension = 4096;

    public static void MapImageServerSoapEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/services/{serviceId}/ImageServer", HandlePostSoapImageServer)
            .WithDisplayName("ArcGIS SOAP ImageServer")
            .WithName("ArcGisSoapImageServer")
            .WithSummary("Read and render an ImageServer through ArcGIS SOAP")
            .WithDescription("Implements the bounded ArcGIS Pro ImageServer SOAP compatibility surface over Honua's canonical raster service.")
            .WithTags("ImageServer")
            .Accepts<string>("text/xml")
            .Produces(StatusCodes.Status200OK, contentType: "text/xml")
            .Produces(StatusCodes.Status400BadRequest, contentType: "text/xml")
            .Produces(StatusCodes.Status404NotFound, contentType: "text/xml")
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
        var request = await ArcGisSoap11RequestParser.ReadOperationAsync(
            context.Request.Body,
            "ImageServer",
            context.RequestAborted).ConfigureAwait(false);
        if (request.Error is not null)
        {
            return CreateSoapFault(request.Error, StatusCodes.Status400BadRequest);
        }

        var operation = request.Operation!;
        var operationNamespace = ArcGisSoap11RequestParser.EsriNamespace;
        var operationName = operation.Name;
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var resolution = await layerResolver.ResolveFirstAccessibleLayerAsync(
            serviceId,
            context,
            cancellationToken).ConfigureAwait(false);
        if (resolution.ErrorResult is not null)
        {
            var statusCode = (resolution.ErrorResult as IStatusCodeHttpResult)?.StatusCode
                ?? StatusCodes.Status404NotFound;
            return CreateSoapFault("Image service was not found or is not accessible.", statusCode);
        }

        try
        {
            if (operationName == operationNamespace + "GetVersion")
            {
                return CreateSoapResponse(
                    operationNamespace,
                    "GetVersionResponse",
                    new XElement("Result", "10.8"));
            }

            if (operationName == operationNamespace + "IsFixedScaleImage")
            {
                return CreateSoapResponse(
                    operationNamespace,
                    "IsFixedScaleImageResponse",
                    new XElement("Result", false));
            }

            if (operationName == operationNamespace + "GetServiceInfo")
            {
                return await HandleGetServiceInfoAsync(
                    serviceId,
                    resolution.LayerId,
                    operationNamespace,
                    rasterStore,
                    cancellationToken).ConfigureAwait(false);
            }

            if (operationName == operationNamespace + "GetFields")
            {
                return CreateSoapResponse(
                    operationNamespace,
                    "GetFieldsResponse",
                    BuildFields());
            }

            if (operationName == operationNamespace + "GetKeyProperties")
            {
                return CreateSoapResponse(
                    operationNamespace,
                    "GetKeyPropertiesResponse",
                    BuildKeyProperties());
            }

            if (operationName == operationNamespace + "GetMetadata")
            {
                return CreateSoapResponse(
                    operationNamespace,
                    "GetMetadataResponse",
                    new XElement("Result", BuildMetadata(serviceId)));
            }

            if (operationName == operationNamespace + "ExportImage")
            {
                return await HandleExportImageAsync(
                    operation,
                    operationNamespace,
                    resolution.LayerId,
                    context,
                    exportHandler,
                    cancellationToken).ConfigureAwait(false);
            }

            if (operationName == operationNamespace + "GetImage")
            {
                return await HandleGetImageAsync(
                    operation,
                    operationNamespace,
                    resolution.LayerId,
                    context,
                    rasterStore,
                    exportHandler,
                    cancellationToken).ConfigureAwait(false);
            }

            return CreateSoapFault(
                $"Unsupported ImageServer operation '{operationName.LocalName}'.",
                StatusCodes.Status400BadRequest);
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
                operationName.LocalName,
                exception);
            return CreateSoapFault(
                "The ImageServer operation could not be completed.",
                StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> HandleGetServiceInfoAsync(
        string serviceId,
        int layerId,
        XNamespace operationNamespace,
        IRasterStore rasterStore,
        CancellationToken cancellationToken)
    {
        var rasters = await rasterStore.ListRastersAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (rasters.Length == 0)
        {
            return CreateSoapFault("Image service has no rasters.", StatusCodes.Status404NotFound);
        }

        var extent = ImageServerMosaicHelpers.ComputeAggregateExtent(rasters);
        if (extent is null)
        {
            return CreateSoapFault("Image service extent is unavailable.", StatusCodes.Status500InternalServerError);
        }

        var referenceRaster = rasters.FirstOrDefault(static raster => raster.Extent.HasValue);
        if (referenceRaster.Width <= 0 || referenceRaster.Height <= 0)
        {
            referenceRaster = rasters[0];
        }

        var pixelSizeX = referenceRaster.Width > 0
            ? (extent.Value.XMax - extent.Value.XMin) / referenceRaster.Width
            : 0;
        var pixelSizeY = referenceRaster.Height > 0
            ? (extent.Value.YMax - extent.Value.YMin) / referenceRaster.Height
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

        return CreateSoapResponse(operationNamespace, "GetServiceInfoResponse", result);
    }

    private static async Task<IResult> HandleExportImageAsync(
        XElement operation,
        XNamespace operationNamespace,
        int layerId,
        HttpContext context,
        ImageServerExportHandler exportHandler,
        CancellationToken cancellationToken)
    {
        if (!TryCreateExportRequest(operation, out var request, out var width, out var height, out var error))
        {
            return CreateSoapFault(error!, StatusCodes.Status400BadRequest);
        }

        var esri = ArcGisSoap11RequestParser.EsriNamespace;
        var returnType = operation
            .Element(esri + "ImageType")?
            .Element(esri + "ImageReturnType")?
            .Value;
        var returnMimeData = string.Equals(returnType, "esriImageReturnMimeData", StringComparison.Ordinal);
        request = CopyWithResponseFormat(request, returnMimeData ? "image" : "json");
        var exportResult = await exportHandler.ExportImageAsync(
            context,
            layerId,
            request,
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
            return CreateSoapResponse(operationNamespace, "ExportImageResponse", mimeResult);
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
            return CreateSoapResponse(operationNamespace, "ExportImageResponse", urlResult);
        }

        return CreateSoapFaultFromResult(exportResult, "Image export failed.");
    }

    private static async Task<IResult> HandleGetImageAsync(
        XElement operation,
        XNamespace operationNamespace,
        int layerId,
        HttpContext context,
        IRasterStore rasterStore,
        ImageServerExportHandler exportHandler,
        CancellationToken cancellationToken)
    {
        if (!TryCreateExportRequest(operation, out var request, out var width, out var height, out var error, requireImageType: false))
        {
            return CreateSoapFault(error!, StatusCodes.Status400BadRequest);
        }

        var rasters = await rasterStore.ListRastersAsync(layerId, cancellationToken).ConfigureAwait(false);
        var referenceRaster = rasters.FirstOrDefault(static raster => raster.Extent.HasValue);
        if (referenceRaster.Id == 0 && rasters.Length > 0)
        {
            referenceRaster = rasters[0];
        }

        if (referenceRaster.Id == 0)
        {
            return CreateSoapFault("Image service has no rasters.", StatusCodes.Status404NotFound);
        }

        if (!string.Equals(referenceRaster.PixelType, "8BUI", StringComparison.OrdinalIgnoreCase))
        {
            return CreateSoapFault(
                "SOAP GetImage currently supports unsigned 8-bit image services.",
                StatusCodes.Status400BadRequest);
        }

        if (request.Compression is not null
            && !string.Equals(request.Compression, "None", StringComparison.OrdinalIgnoreCase))
        {
            return CreateSoapFault(
                "SOAP GetImage supports uncompressed Esri pixel blocks only.",
                StatusCodes.Status400BadRequest);
        }

        var bandCount = ResolveGetImageBandCount(request.BandIds, referenceRaster.BandCount);
        if (bandCount is < 1 or > 4)
        {
            return CreateSoapFault(
                "SOAP GetImage currently supports one to four rendered bands.",
                StatusCodes.Status400BadRequest);
        }

        request = CopyWithResponseFormat(request, "image");
        var exportResult = await exportHandler.ExportImageAsync(
            context,
            layerId,
            request,
            cancellationToken).ConfigureAwait(false);
        if (exportResult is Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult
            { FileContents: var imageData })
        {
            if (!TryBuildEsriPixelBlock(imageData.Span, width, height, bandCount, out var pixelBlock))
            {
                return CreateSoapFault(
                    "The canonical raster renderer returned an invalid image payload.",
                    StatusCodes.Status500InternalServerError);
            }

            return CreateSoapResponse(
                operationNamespace,
                "GetImageResponse",
                new XElement("Result", Convert.ToBase64String(pixelBlock)));
        }

        return CreateSoapFaultFromResult(exportResult, "Image export failed.");
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

        var esri = ArcGisSoap11RequestParser.EsriNamespace;
        var description = operation.Element(esri + "ImageDescription");
        if (description is null)
        {
            error = "ImageDescription is required.";
            return false;
        }

        var extent = description.Element(esri + "Extent");
        if (extent is null ||
            !TryReadFiniteDouble(extent, esri + "XMin", out var xMin) ||
            !TryReadFiniteDouble(extent, esri + "YMin", out var yMin) ||
            !TryReadFiniteDouble(extent, esri + "XMax", out var xMax) ||
            !TryReadFiniteDouble(extent, esri + "YMax", out var yMax) ||
            xMin >= xMax || yMin >= yMax)
        {
            error = "ImageDescription extent must be a finite, non-empty envelope.";
            return false;
        }

        if (!TryReadInt(description, esri + "Width", out width) ||
            !TryReadInt(description, esri + "Height", out height) ||
            width is < 1 or > MaxImageDimension ||
            height is < 1 or > MaxImageDimension)
        {
            error = $"ImageDescription width and height must be between 1 and {MaxImageDimension}.";
            return false;
        }

        var imageType = operation.Element(esri + "ImageType");
        if (requireImageType && imageType is null)
        {
            error = "ImageType is required.";
            return false;
        }

        var imageFormat = imageType?.Element(esri + "ImageFormat")?.Value;
        if (requireImageType && string.IsNullOrWhiteSpace(imageFormat))
        {
            error = "ImageFormat is required.";
            return false;
        }

        var format = MapImageFormat(imageFormat);
        if (format is null)
        {
            error = "ImageFormat must be PNG, JPG, or TIFF.";
            return false;
        }

        var imageReturnType = imageType?.Element(esri + "ImageReturnType")?.Value;
        if (requireImageType && string.IsNullOrWhiteSpace(imageReturnType))
        {
            error = "ImageReturnType is required.";
            return false;
        }

        if (requireImageType
            && imageReturnType is not ("esriImageReturnURL" or "esriImageReturnMimeData"))
        {
            error = "ImageReturnType must be URL or MIME data.";
            return false;
        }

        var spatialReference = extent.Element(esri + "SpatialReference");
        var wkid = spatialReference?.Element(esri + "LatestWKID")?.Value
            ?? spatialReference?.Element(esri + "WKID")?.Value;

        request = new ExportImageRequest
        {
            Bbox = FormattableString.Invariant($"{xMin},{yMin},{xMax},{yMax}"),
            Size = FormattableString.Invariant($"{width},{height}"),
            BboxSr = wkid,
            ImageSr = wkid,
            Format = format,
            PixelType = NormalizeOptionalValue(description.Element(esri + "PixelType")?.Value),
            NoData = NormalizeOptionalValue(description.Element(esri + "NoData")?.Value),
            Interpolation = NormalizeOptionalValue(description.Element(esri + "Interpolation")?.Value),
            Compression = NormalizeOptionalValue(description.Element(esri + "Compression")?.Value),
            CompressionQuality = TryReadInt(description, esri + "CompressionQuality", out var quality) ? quality : 75,
            BandIds = string.Join(",", description.Descendants()
                .Where(element => element.Name == esri + "Int")
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

    private static IResult CreateSoapResponse(XNamespace operationNamespace, string responseName, XElement result)
    {
        var soap = ArcGisSoap11RequestParser.EnvelopeNamespace;
        XNamespace xsi = XmlSchemaInstanceNamespace;
        XNamespace xsd = XmlSchemaNamespace;
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
            contentType: SoapContentType,
            contentEncoding: Encoding.UTF8);
    }

    private static IResult CreateSoapFaultFromResult(IResult result, string message)
    {
        var statusCode = (result as IStatusCodeHttpResult)?.StatusCode
            ?? StatusCodes.Status500InternalServerError;
        return CreateSoapFault(message, statusCode);
    }

    private static IResult CreateSoapFault(string message, int statusCode)
    {
        var soap = ArcGisSoap11RequestParser.EnvelopeNamespace;
        var response = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soap", soap.NamespaceName),
                new XElement(
                    soap + "Body",
                    new XElement(
                        soap + "Fault",
                        new XElement("faultcode", statusCode >= 500 ? "soap:Server" : "soap:Client"),
                        new XElement("faultstring", message)))));

        return Results.Content(
            response.ToString(SaveOptions.DisableFormatting),
            contentType: SoapContentType,
            contentEncoding: Encoding.UTF8,
            statusCode: statusCode);
    }

    private static bool TryReadFiniteDouble(XElement parent, XName childName, out double value)
        => double.TryParse(
               parent.Element(childName)?.Value,
               NumberStyles.Float,
               CultureInfo.InvariantCulture,
               out value)
           && double.IsFinite(value);

    private static bool TryReadInt(XElement parent, XName childName, out int value)
        => int.TryParse(
            parent.Element(childName)?.Value,
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
