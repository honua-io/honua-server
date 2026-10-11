// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Microsoft.Extensions.Primitives;
using static Honua.Protocols.GeoServices.Soap.ArcGisSoapProtocol;

namespace Honua.Protocols.GeoServices.ImageServer;

internal static partial class ImageServerSoapEndpoints
{
    internal const int MaxKeyPropertiesSelectedBands = 256;
    private readonly record struct ServiceInfoExtras(
        bool SupportsTime,
        XElement? StartTimeFieldName,
        XElement? EndTimeFieldName,
        XElement? TimeExtent,
        XElement? Histograms,
        IResult? Error)
    {
        public static ServiceInfoExtras Failed(IResult error) => new(false, null, null, null, null, error);
    }

    private static async Task<IResult> HandleGetKeyPropertiesAsync(
        XElement operation,
        SoapRasterRequestContext request,
        string responseName,
        CancellationToken cancellationToken)
    {
        var revalidation = await RevalidateRasterPublicationAsync(
            request.Resolution,
            request.HttpContext,
            request.HttpContext.RequestServices.GetRequiredService<IImageServerLayerResolver>(),
            AuthorizationOperation.Metadata,
            request.SoapNamespace,
            cancellationToken).ConfigureAwait(false);
        if (revalidation.ErrorResult is not null)
        {
            return revalidation.ErrorResult;
        }

        var rasterStore = request.HttpContext.RequestServices.GetRequiredService<IRasterStore>();
        var rasters = await rasterStore.ListRastersAsync(
            revalidation.Resolution.LayerId, cancellationToken).ConfigureAwait(false);
        if (rasters.Length == 0)
        {
            return CreateSoapFault("Image service has no rasters.", StatusCodes.Status404NotFound, request.SoapNamespace);
        }

        var extent = ImageServerMosaicHelpers.ComputeAggregateExtent(rasters);
        if (extent is null)
        {
            return CreateSoapFault("Image service extent is unavailable.", StatusCodes.Status500InternalServerError, request.SoapNamespace);
        }

        // Use the same reference raster and canonical mosaic policy as GetServiceInfo.
        // RasterInfo is the native key-property carrier for statistics and histograms.
        var referenceRaster = SelectReferenceRaster(rasters);
        if (!TryReadKeyPropertiesBands(operation, referenceRaster.BandCount, out var selectedBands,
                out var renderingError, out var renderingStatus))
        {
            return CreateSoapFault(renderingError!, renderingStatus, request.SoapNamespace);
        }

        var statistics = await ReadServiceStatisticsAsync(
            request.HttpContext, revalidation.Resolution.LayerId, rasters,
            revalidation.Resolution.MergeStrategy, cancellationToken,
            selectedBands?.Distinct().Order().ToArray()).ConfigureAwait(false);
        var histograms = await ReadServiceHistogramsAsync(
            request.HttpContext, revalidation.Resolution.LayerId, rasters,
            revalidation.Resolution.MergeStrategy, request.SoapNamespace, cancellationToken,
            selectedBands?.Distinct().Order().ToArray()).ConfigureAwait(false);
        if (histograms.Error is not null)
        {
            return histograms.Error;
        }

        return CreateSoapResponse(
            request.SoapNamespace,
            request.OperationNamespace,
            responseName,
            BuildKeyPropertiesResult(
                BuildSelectedKeyProperties(referenceRaster, selectedBands),
                BuildKeyPropertiesRasterInfo(referenceRaster, extent.Value, statistics, histograms.Histograms!, selectedBands)));
    }

    private static async Task<IResult> HandleGetRasterKeyPropertiesAsync(
        XElement operation,
        SoapRasterRequestContext request,
        CancellationToken cancellationToken)
    {
        if (!TryReadInt(operation, "RID", out var rasterId))
        {
            return CreateSoapFault(
                "RID is required.",
                StatusCodes.Status400BadRequest,
                request.SoapNamespace);
        }

        var revalidation = await RevalidateMetadataAsync(request, cancellationToken).ConfigureAwait(false);
        if (revalidation.Error is not null)
        {
            return revalidation.Error;
        }

        var handler = request.HttpContext.RequestServices.GetRequiredService<ImageServerRasterItemHandler>();
        var result = await handler.GetKeyPropertiesAsync(
            request.HttpContext,
            revalidation.LayerId,
            rasterId,
            cancellationToken).ConfigureAwait(false);
        if (!TryGetPayload<KeyPropertiesResponse>(result, out var properties))
        {
            return CreateSoapFaultFromResult(result, "Raster key properties could not be read.", request.SoapNamespace);
        }

        return CreateSoapResponse(
            request.SoapNamespace,
            request.OperationNamespace,
            "GetRasterKeyPropertiesResponse",
            BuildKeyPropertiesResult(properties));
    }

    private static async Task<IResult> HandleComputeHistogramsAsync(
        XElement operation,
        SoapRasterRequestContext request,
        CancellationToken cancellationToken)
    {
        // A request with no geometry, mosaic rule, pixel size, or rendering rule is the
        // stored service histogram (REST /histograms). Any of those arguments is a
        // computeHistograms request and must reach that handler instead of being dropped.
        if (!HasComputeHistogramArguments(operation))
        {
            var revalidation = await RevalidateMetadataAsync(request, cancellationToken).ConfigureAwait(false);
            if (revalidation.Error is not null)
            {
                return revalidation.Error;
            }

            var histograms = await ReadServiceHistogramsAsync(
                request.HttpContext,
                revalidation.LayerId,
                request.SoapNamespace,
                cancellationToken).ConfigureAwait(false);
            if (histograms.Error is not null)
            {
                return histograms.Error;
            }

            return HistogramSoapResponse(request, histograms.Histograms!);
        }

        if (!TryBuildComputeHistogramQuery(operation, out var values, out var error))
        {
            return CreateSoapFault(error!, StatusCodes.Status400BadRequest, request.SoapNamespace);
        }

        var computeRevalidation = await RevalidateMetadataAsync(request, cancellationToken).ConfigureAwait(false);
        if (computeRevalidation.Error is not null)
        {
            return computeRevalidation.Error;
        }

        var handler = request.HttpContext.RequestServices.GetRequiredService<ImageServerStatisticsHistogramsHandler>();
        var result = await handler.ComputeHistogramsAsync(
            request.HttpContext,
            computeRevalidation.LayerId,
            values,
            cancellationToken).ConfigureAwait(false);
        if (!TryGetPayload<ComputeHistogramsResponse>(result, out var computed))
        {
            return CreateSoapFaultFromResult(result, "Image histograms could not be read.", request.SoapNamespace);
        }

        return HistogramSoapResponse(request, computed.Histograms);
    }

    private static bool HasComputeHistogramArguments(XElement operation)
        => IsPresent(operation, "Geometry")
            || IsPresent(operation, "MosaicRule")
            || IsPresent(operation, "PixelSize")
            || IsPresent(operation, "RenderingRule");

    private static bool IsPresent(XElement parent, string localName)
    {
        var child = DirectChild(parent, localName);
        return child is not null && !IsNilElement(child);
    }

    private static bool TryBuildComputeHistogramQuery(
        XElement operation,
        out Dictionary<string, StringValues> values,
        out string? error)
    {
        values = new Dictionary<string, StringValues>(StringComparer.Ordinal) { ["f"] = "json" };
        error = null;

        if (IsPresent(operation, "Geometry"))
        {
            if (!TrySerializeSoapGeometry(
                    DirectChild(operation, "Geometry")!,
                    out var geometry,
                    out var geometryType,
                    out var wkid,
                    out error))
            {
                return false;
            }

            values["geometry"] = geometry;
            values["geometryType"] = geometryType;
            if (wkid is int srid)
            {
                values["inSR"] = srid.ToString(CultureInfo.InvariantCulture);
            }
        }

        if (IsPresent(operation, "MosaicRule"))
        {
            if (!TrySerializeSoapMosaicRule(operation, out var mosaicRule, out error))
            {
                return false;
            }

            if (mosaicRule is not null)
            {
                values["mosaicRule"] = mosaicRule;
            }
        }

        if (IsPresent(operation, "PixelSize"))
        {
            if (!TrySerializeSoapPixelSize(DirectChild(operation, "PixelSize")!, out var pixelSize, out error))
            {
                return false;
            }

            values["pixelSize"] = pixelSize;
        }

        if (IsPresent(operation, "RenderingRule"))
        {
            if (!TrySerializeSoapRenderingRule(DirectChild(operation, "RenderingRule")!, out var renderingRule, out error))
            {
                return false;
            }

            values["renderingRule"] = renderingRule;
        }

        return error is null;
    }

    private static IResult HistogramSoapResponse(SoapRasterRequestContext request, BandHistogram[] histograms)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        return CreateSoapResponse(
            request.SoapNamespace,
            request.OperationNamespace,
            "ComputeHistogramsResponse",
            new XElement(
                "Result",
                new XAttribute(xsi + "type", "tns:ArrayOfRasterHistogram"),
                histograms.Select(BuildRasterHistogram)));
    }

    private static bool TrySerializeSoapPixelSize(XElement pixelSize, out string json, out string? error)
    {
        json = string.Empty;
        error = null;
        if (!TryReadSoapDouble(pixelSize, "X", out var x) || !TryReadSoapDouble(pixelSize, "Y", out var y))
        {
            error = "PixelSize requires X and Y.";
            return false;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", x);
            writer.WriteNumber("y", y);
            writer.WriteEndObject();
        }

        json = Encoding.UTF8.GetString(stream.ToArray());
        return true;
    }

    private static bool TrySerializeSoapRenderingRule(XElement renderingRule, out string json, out string? error)
    {
        json = string.Empty;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            if (!TryWriteRenderingFunction(writer, renderingRule, 0, out error))
            {
                return false;
            }
        }

        json = Encoding.UTF8.GetString(stream.ToArray());
        return true;
    }

    private static bool TryWriteRenderingFunction(Utf8JsonWriter writer, XElement renderingRule,
        int depth, out string? error)
    {
        error = null;
        if (depth > 16)
        {
            error = "RenderingRule structured arguments exceed the supported depth.";
            return false;
        }

        var function = DirectChild(renderingRule, "Function") ?? renderingRule;
        var name = DirectChildText(function, "FunctionName")
            ?? DirectChildText(function, "Name")
            ?? DirectChildText(renderingRule, "Name");
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "RenderingRule requires a function name.";
            return false;
        }

        var arguments = DirectChild(function, "Arguments") ?? DirectChild(renderingRule, "Arguments");
        writer.WriteStartObject();
        writer.WriteString("rasterFunction", name.Trim());
        if (arguments is not null && !IsNilElement(arguments))
        {
            writer.WritePropertyName("rasterFunctionArguments");
            writer.WriteStartObject();
            if (!TryWriteRenderingArguments(writer, arguments, depth + 1, out error))
            {
                return false;
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        return true;
    }

    private static bool TryWriteRenderingArguments(Utf8JsonWriter writer, XElement arguments,
        int depth, out string? error)
    {
        error = null;
        var properties = arguments.Descendants()
            .Where(element => element.Name.LocalName == "PropertySetProperty"
                && element.Ancestors().TakeWhile(parent => parent != arguments)
                    .All(parent => parent.Name.LocalName is "PropertyArray" or "Properties" or "ArrayOfPropertySetProperty"))
            .ToArray();
        if (properties.Length > 0)
        {
            foreach (var property in properties)
            {
                var key = DirectChildText(property, "Key");
                var value = DirectChild(property, "Value");
                if (string.IsNullOrWhiteSpace(key) || value is null)
                {
                    error = "RenderingRule argument properties require a Key and Value.";
                    return false;
                }

                // Bound amplification before serializing/mapping BandIds, including
                // duplicates that intentionally remain distinct output bands.
                if (string.Equals(key.Trim(), "BandIds", StringComparison.OrdinalIgnoreCase)
                    && value.Elements().Take(MaxKeyPropertiesSelectedBands + 1).Count() > MaxKeyPropertiesSelectedBands)
                {
                    error = $"RenderingRule BandIds cannot select more than {MaxKeyPropertiesSelectedBands} output bands.";
                    return false;
                }

                writer.WritePropertyName(key.Trim());
                if (!TryWriteRenderingArgument(writer, value, depth, out error))
                {
                    return false;
                }
            }

            return true;
        }

        foreach (var child in arguments.Elements())
        {
            writer.WritePropertyName(child.Name.LocalName);
            if (string.Equals(child.Name.LocalName, "BandIds", StringComparison.OrdinalIgnoreCase)
                && child.Elements().Take(MaxKeyPropertiesSelectedBands + 1).Count() > MaxKeyPropertiesSelectedBands)
            {
                error = $"RenderingRule BandIds cannot select more than {MaxKeyPropertiesSelectedBands} output bands.";
                return false;
            }

            if (!TryWriteRenderingArgument(writer, child, depth, out error))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryWriteRenderingArgument(Utf8JsonWriter writer, XElement value,
        int depth, out string? error)
    {
        error = null;
        if (depth > 16)
        {
            error = "RenderingRule structured arguments exceed the supported depth.";
            return false;
        }

        if (!value.HasElements)
        {
            WriteSoapScalar(writer, value);
            return true;
        }

        var type = SoapTypeName(value);
        if (type is "Envelope" or "EnvelopeN" or "Polygon" or "PolygonN")
        {
            if (!TrySerializeSoapGeometry(value, out var geometry, out _, out _, out error))
            {
                return false;
            }

            using var geometryDocument = JsonDocument.Parse(geometry);
            geometryDocument.RootElement.WriteTo(writer);
            return true;
        }

        if (DirectChild(value, "Function") is not null || DirectChild(value, "FunctionName") is not null
            || ((type is "RasterFunction" or "RasterFunctionTemplate") && DirectChild(value, "Name") is not null))
        {
            return TryWriteRenderingFunction(writer, value, depth + 1, out error);
        }

        if (type == "PropertySet")
        {
            writer.WriteStartObject();
            if (!TryWriteRenderingArguments(writer, value, depth + 1, out error))
            {
                return false;
            }

            writer.WriteEndObject();
            return true;
        }

        var itemType = type switch
        {
            "ArrayOfInt" => "Int",
            "ArrayOfDouble" => "Double",
            "ArrayOfString" => "String",
            "ArrayOfArrayOfInt" => "ArrayOfInt",
            "ArrayOfArrayOfDouble" => "ArrayOfDouble",
            _ => null
        };
        if (itemType is null || value.Elements().Any(item => item.Name.LocalName != itemType
            || (itemType == "Int" && (item.HasElements
                || !int.TryParse(item.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            || (itemType == "Double" && (item.HasElements
                || !double.TryParse(item.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                || !double.IsFinite(number)))
            || (itemType == "String" && item.HasElements)))
        {
            error = "RenderingRule structured argument type or contents are invalid.";
            return false;
        }

        writer.WriteStartArray();
        foreach (var item in value.Elements())
        {
            if (itemType == "Int")
            {
                writer.WriteNumberValue(int.Parse(item.Value, NumberStyles.Integer, CultureInfo.InvariantCulture));
            }
            else if (itemType == "Double")
            {
                writer.WriteNumberValue(double.Parse(item.Value, NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            else if (itemType == "String")
            {
                writer.WriteStringValue(item.Value);
            }
            else if (!TryWriteRenderingArgument(writer,
                new XElement(item.Name, new XAttribute(XNamespace.Get(XmlSchemaInstanceNamespace) + "type", "tns:" + itemType), item.Nodes()),
                depth + 1, out error))
            {
                return false;
            }
        }
        writer.WriteEndArray();
        return true;
    }

    internal static bool TryReadKeyPropertiesBands(
        XElement operation, int bandCount, out int[]? bands, out string? error, out int status)
    {
        bands = null;
        error = null;
        status = StatusCodes.Status400BadRequest;
        if (!IsPresent(operation, "RenderingRule"))
        {
            return true;
        }

        if (!TrySerializeSoapRenderingRule(DirectChild(operation, "RenderingRule")!, out var json, out error))
        {
            return false;
        }

        var document = JsonSerializer.Deserialize(json, ImageServerJsonContext.Default.RasterFunctionDocument)!;
        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);
        if (!mapping.Supported)
        {
            error = mapping.Reason;
            status = mapping.IsNotImplemented ? StatusCodes.Status501NotImplemented : StatusCodes.Status400BadRequest;
            return false;
        }

        // Band selection changes metadata without processing pixel values. Other
        // functions require rendered statistics; never report the stored values as those.
        if (mapping.Stretch is not null || mapping.Colormap is not null || mapping.ClipRegion is not null
            || mapping.BandArithmetic is not null || mapping.Terrain is not null)
        {
            error = "Key properties for pixel-transforming rendering rules are not implemented.";
            status = StatusCodes.Status501NotImplemented;
            return false;
        }

        if (mapping.Bands is { Length: > MaxKeyPropertiesSelectedBands })
        {
            error = $"RenderingRule BandIds cannot select more than {MaxKeyPropertiesSelectedBands} output bands.";
            return false;
        }

        if (mapping.Bands is { } selected && selected.Any(band => band < 1 || band > bandCount))
        {
            error = "RenderingRule BandIds exceeds the source band count.";
            return false;
        }

        bands = mapping.Bands;
        return true;
    }

    internal static KeyPropertiesResponse BuildSelectedKeyProperties(RasterInfo raster, int[]? bands)
    {
        var properties = ImageServerKeyPropertiesHandler.BuildResponse(raster);
        if (bands is null)
        {
            return properties;
        }

        return new KeyPropertiesResponse
        {
            BandProperties = bands.Select(band => properties.BandProperties[band - 1]).ToArray(),
            BandCount = bands.Length,
            LowCellSize = properties.LowCellSize,
            HighCellSize = properties.HighCellSize,
            MaxCellSize = properties.MaxCellSize,
            ConfigKeyword = properties.ConfigKeyword,
            BandDefinitionKeyword = properties.BandDefinitionKeyword,
            DataType = properties.DataType,
            NoDataValue = properties.NoDataValue,
        };
    }

    private static void WriteSoapScalar(Utf8JsonWriter writer, XElement value)
    {
        var text = value.Value.Trim();
        var typeName = SoapTypeName(value);
        if (typeName is "boolean" or "bool")
        {
            writer.WriteBooleanValue(text is "1" || bool.TryParse(text, out var typed) && typed);
            return;
        }

        if (typeName is "double" or "float" or "decimal"
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var typedNumber))
        {
            writer.WriteNumberValue(typedNumber);
            return;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
            && text.IndexOf('.') < 0)
        {
            writer.WriteNumberValue(integer);
            return;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && text.IndexOfAny(['.', 'e', 'E']) >= 0)
        {
            writer.WriteNumberValue(number);
            return;
        }

        if (bool.TryParse(text, out var boolean))
        {
            writer.WriteBooleanValue(boolean);
            return;
        }

        writer.WriteStringValue(text);
    }

    private static async Task<IResult> HandleGetMultidimensionalInfoAsync(
        SoapRasterRequestContext request,
        CancellationToken cancellationToken)
    {
        var revalidation = await RevalidateMetadataAsync(request, cancellationToken).ConfigureAwait(false);
        if (revalidation.Error is not null)
        {
            return revalidation.Error;
        }

        var info = await ReadMultidimensionalInfoAsync(
            request.HttpContext,
            revalidation.LayerId,
            cancellationToken).ConfigureAwait(false);
        return CreateSoapResponse(
            request.SoapNamespace,
            request.OperationNamespace,
            "GetMultidimensionalInfoResponse",
            BuildMultidimensionalResult(info));
    }

    private static async Task<ServiceInfoExtras> BuildServiceInfoExtrasAsync(
        HttpContext context,
        int layerId,
        IReadOnlyList<RasterInfo> rasters,
        RasterMergeStrategy mergeStrategy,
        XNamespace soapNamespace,
        CancellationToken cancellationToken)
    {
        var info = await ReadMultidimensionalInfoAsync(context, layerId, cancellationToken).ConfigureAwait(false);
        var histograms = await ReadServiceHistogramsAsync(
            context,
            layerId,
            rasters,
            mergeStrategy,
            soapNamespace,
            cancellationToken).ConfigureAwait(false);
        if (histograms.Error is not null)
        {
            return ServiceInfoExtras.Failed(histograms.Error);
        }

        var supportsTimeDimension = info?.Variables.Any(static variable =>
            variable.Dimensions.Any(static dimension =>
                string.Equals(dimension.Name, "StdTime", StringComparison.Ordinal))) == true;
        var time = await ReadServiceTimeAsync(context, layerId, rasters, cancellationToken).ConfigureAwait(false);
        XNamespace xsi = XmlSchemaInstanceNamespace;
        return new ServiceInfoExtras(
            supportsTimeDimension || time.HasTime,
            time.StartTimeField is null ? null : new XElement("StartTimeFieldName", time.StartTimeField),
            time.EndTimeField is null ? null : new XElement("EndTimeFieldName", time.EndTimeField),
            time.StartUnixMilliseconds is long start && time.EndUnixMilliseconds is long end
                ? new XElement(
                    "TimeExtent",
                    new XAttribute(xsi + "type", "tns:TimeExtent"),
                    new XElement("Start", FormatUnixTime(start)),
                    new XElement("End", FormatUnixTime(end)))
                : null,
            new XElement(
                "Histograms",
                new XAttribute(xsi + "type", "tns:ArrayOfRasterHistogram"),
                histograms.Histograms!.Select(BuildRasterHistogram)),
            null);
    }

    private static XElement BuildKeyPropertiesResult(KeyPropertiesResponse properties, XElement? rasterInfo = null)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        // ArcGIS SOAP exposes the band-property IArray as ArrayOfArgument;
        // each Argument remains a typed PropertySet for native deserialization.
        var items = new List<XElement>
        {
            new(
                "PropertySetProperty",
                new XAttribute(xsi + "type", "tns:PropertySetProperty"),
                new XElement("Key", "BandProperties"),
                new XElement(
                    "Value",
                    new XAttribute(xsi + "type", "tns:ArrayOfArgument"),
                    properties.BandProperties.Select(BuildBandPropertySet))),
        };
        if (rasterInfo is not null)
        {
            items.Add(new XElement(
                "PropertySetProperty",
                new XAttribute(xsi + "type", "tns:PropertySetProperty"),
                new XElement("Key", "RasterInfo"),
                rasterInfo));
        }

        AddDoubleProperty(items, "LowCellSize", properties.LowCellSize, xsi);
        AddDoubleProperty(items, "HighCellSize", properties.HighCellSize, xsi);
        AddDoubleProperty(items, "MaxCellSize", properties.MaxCellSize, xsi);
        items.Add(BuildProperty("ConfigKeyword", properties.ConfigKeyword, "xsd:string", xsi));
        items.Add(BuildProperty("BandDefinitionKeyword", properties.BandDefinitionKeyword, "xsd:string", xsi));
        if (properties.DataType is not null)
        {
            items.Add(BuildProperty("DataType", properties.DataType, "xsd:string", xsi));
        }

        items.Add(BuildProperty("BandCount", properties.BandCount.ToString(CultureInfo.InvariantCulture), "xsd:int", xsi));
        AddDoubleProperty(items, "NoDataValue", properties.NoDataValue, xsi);

        return new XElement(
            "Result",
            new XAttribute(xsi + "type", "tns:PropertySet"),
            new XElement(
                "PropertyArray",
                new XAttribute(xsi + "type", "tns:ArrayOfPropertySetProperty"),
                items));
    }

    private static async Task<RasterStatistics[]> ReadServiceStatisticsAsync(
        HttpContext context,
        int layerId,
        RasterInfo[] rasters,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken,
        int[]? bands = null)
    {
        var rasterIds = rasters.Select(static raster => raster.Id).ToArray();
        var schemaName = context.RequestServices.GetService<ISchemaContext>()?.CurrentSchema;
        var statistics = await ImageServerStatisticsBudget.ResolveAsync(
            context.RequestServices.GetRequiredService<IServiceScopeFactory>(),
            ImageServerStatisticsBudget.CreateStatisticsOperationKey(
                schemaName,
                layerId,
                rasterIds,
                mergeStrategy,
                bands),
            schemaName,
            (services, ct) =>
            {
                var scopedRasterStore = services.GetRequiredService<IRasterStore>();
                return rasters.Length == 1
                    ? scopedRasterStore.GetStatisticsAsync(
                        layerId,
                        rasters[0].Id,
                        bands: bands,
                        cancellationToken: ct)
                    : scopedRasterStore.GetMosaicStatisticsAsync(
                        layerId,
                        rasterIds,
                        mergeStrategy,
                        bands: bands,
                        cancellationToken: ct);
            },
            onBudgetExceeded: () => context.Response.Headers.CacheControl = "no-store",
            cancellationToken).ConfigureAwait(false);
        return statistics.OrderBy(static statistic => statistic.Band).ToArray();
    }

    internal static XElement BuildKeyPropertiesRasterInfo(
        RasterInfo referenceRaster,
        RasterExtent extent,
        RasterStatistics[] statistics,
        BandHistogram[] histograms,
        int[]? selectedBands = null)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        var (pixelSizeX, pixelSizeY) = ImageServerKeyPropertiesHandler.ComputePixelSizes(referenceRaster);
        var nativeExtent = BuildEnvelope(extent);
        nativeExtent.Name = "NativeExtent";
        var value = new XElement(
            "Value",
            new XAttribute(xsi + "type", "tns:RasterInfo"),
            new XElement("PixelSizeX", FormatDouble(pixelSizeX)),
            new XElement("PixelSizeY", FormatDouble(pixelSizeY)),
            nativeExtent,
            new XElement("BandCount", selectedBands?.Length ?? referenceRaster.BandCount),
            new XElement("PixelType", MapPixelType(referenceRaster.PixelType)),
            BuildNoData(referenceRaster.NoDataValue));

        // These zero-based extensible members match the native RasterInfo SOAP shape.
        // Do not fabricate median/mode, sample counts, sums, or absent-band statistics.
        // Index once. Scanning the full metadata arrays for every output band is
        // quadratic on an unselected hyperspectral raster.
        var statisticsByBand = IndexByBand(statistics, static statistic => statistic.Band);
        var histogramsByBand = IndexByBand(histograms, static histogram => histogram.SourceBand);
        var bands = selectedBands ?? Enumerable.Range(1, referenceRaster.BandCount).ToArray();
        for (var outputBand = 0; outputBand < bands.Length; outputBand++)
        {
            var sourceBand = bands[outputBand];
            if (statisticsByBand.TryGetValue(sourceBand, out var bandStatistics))
            {
                foreach (var statistic in bandStatistics)
                {
                    if (statistic.MinValue is null || statistic.MaxValue is null)
                    {
                        continue;
                    }

                    value.Add(new XElement(
                        $"RasterStatistics_{outputBand}",
                        new XAttribute(xsi + "type", "tns:RasterStatistics"),
                        new XElement("Min", FormatDouble(statistic.MinValue.Value)),
                        new XElement("Max", FormatDouble(statistic.MaxValue.Value)),
                        statistic.MeanValue is double mean ? new XElement("Mean", FormatDouble(mean)) : null,
                        statistic.StandardDeviation is double standardDeviation
                            ? new XElement("StandardDeviation", FormatDouble(standardDeviation)) : null));
                }
            }

            if (histogramsByBand.TryGetValue(sourceBand, out var bandHistograms))
            {
                foreach (var bandHistogram in bandHistograms)
                {
                    var histogram = BuildRasterHistogram(bandHistogram);
                    histogram.Name = $"RasterHistogram_{outputBand}";
                    value.Add(histogram);
                }
            }
        }

        return value;
    }

    private static Dictionary<int, List<TItem>> IndexByBand<TItem>(IReadOnlyList<TItem> items, Func<TItem, int> bandOf)
    {
        var index = new Dictionary<int, List<TItem>>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var band = bandOf(item);
            if (!index.TryGetValue(band, out var matches))
            {
                matches = [];
                index.Add(band, matches);
            }

            matches.Add(item);
        }

        return index;
    }

    private static XElement BuildBandPropertySet(BandProperty band)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        return new XElement(
            "Argument",
            new XAttribute(xsi + "type", "tns:PropertySet"),
            new XElement(
                "PropertyArray",
                new XAttribute(xsi + "type", "tns:ArrayOfPropertySetProperty"),
                BuildProperty("BandName", band.BandName, "xsd:string", xsi),
                BuildProperty("PixelType", band.PixelType ?? string.Empty, "xsd:string", xsi)));
    }

    private static void AddDoubleProperty(List<XElement> items, string key, double? value, XNamespace xsi)
    {
        if (value.HasValue)
        {
            items.Add(BuildProperty(key, FormatDouble(value.Value), "xsd:double", xsi));
        }
    }

    private static XElement BuildRasterHistogram(BandHistogram histogram)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        return new XElement(
            "RasterHistogram",
            new XAttribute(xsi + "type", "tns:RasterHistogram"),
            new XElement("Size", histogram.Size),
            new XElement("Min", FormatDouble(histogram.Min)),
            new XElement("Max", FormatDouble(histogram.Max)),
            new XElement(
                "Counts",
                new XAttribute(xsi + "type", "tns:ArrayOfDouble"),
                histogram.Counts.Select(static count => new XElement(
                    "Double",
                    count.ToString(CultureInfo.InvariantCulture)))));
    }

    private static async Task<ImageServerMultidimensionalInfo?> ReadMultidimensionalInfoAsync(
        HttpContext context,
        int layerId,
        CancellationToken cancellationToken)
    {
        var builder = context.RequestServices.GetRequiredService<IImageServerMultidimensionalInfoBuilder>();
        return await builder.BuildAsync(layerId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(BandHistogram[]? Histograms, IResult? Error)> ReadServiceHistogramsAsync(
        HttpContext context,
        int layerId,
        IReadOnlyList<RasterInfo> rasters,
        RasterMergeStrategy mergeStrategy,
        XNamespace soapNamespace,
        CancellationToken cancellationToken,
        int[]? bands = null)
    {
        if (rasters.Count == 0)
        {
            return (null, CreateSoapFault("Image service has no rasters.", StatusCodes.Status404NotFound, soapNamespace));
        }

        var handler = context.RequestServices.GetRequiredService<ImageServerRasterMetadataHandler>();
        return (await handler.ReadHistogramsAsync(layerId, rasters, mergeStrategy,
            cancellationToken, bands).ConfigureAwait(false), null);
    }

    private static async Task<(BandHistogram[]? Histograms, IResult? Error)> ReadServiceHistogramsAsync(
        HttpContext context,
        int layerId,
        XNamespace soapNamespace,
        CancellationToken cancellationToken)
    {
        var handler = context.RequestServices.GetRequiredService<ImageServerRasterMetadataHandler>();
        var result = await handler.GetHistogramsAsync(context, layerId, cancellationToken).ConfigureAwait(false);
        if (!TryGetPayload<HistogramsResourceResponse>(result, out var response))
        {
            return (null, CreateSoapFaultFromResult(
                result,
                "Image histograms could not be read.",
                soapNamespace));
        }

        return (response.Histograms, null);
    }

    private static async Task<(bool HasTime, string? StartTimeField, string? EndTimeField, long? StartUnixMilliseconds, long? EndUnixMilliseconds)>
        ReadServiceTimeAsync(
            HttpContext context,
            int layerId,
            IReadOnlyList<RasterInfo> rasters,
            CancellationToken cancellationToken)
    {
        var graph = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graph.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var resolved = ImageServerV2Lookups.FindByStorageLayerId(snapshot, layerId, context);
        var hints = ImageServerV2Lookups.ReadTimeFieldHints(resolved?.Resource);
        var hasAcquisitionDates = rasters.Any(static raster => raster.AcquisitionDate.HasValue);
        var extent = hasAcquisitionDates ? ImageServerMosaicHelpers.CreateTimeExtent(rasters) : null;
        var hasDeclaredFields = !string.IsNullOrWhiteSpace(hints.StartTimeField)
            || !string.IsNullOrWhiteSpace(hints.EndTimeField)
            || !string.IsNullOrWhiteSpace(hints.TrackIdField);
        if (!hasDeclaredFields && extent is null)
        {
            return (false, null, null, null, null);
        }

        return (
            true,
            hints.StartTimeField ?? (extent is null ? null : "AcquisitionDate"),
            hints.EndTimeField,
            extent is { Length: > 0 } ? extent[0] : null,
            extent is { Length: > 1 } ? extent[1] : null);
    }

    private static async Task<(int LayerId, IResult? Error)> RevalidateMetadataAsync(
        SoapRasterRequestContext request,
        CancellationToken cancellationToken)
    {
        var revalidation = await RevalidateRasterPublicationAsync(
            request.Resolution,
            request.HttpContext,
            request.HttpContext.RequestServices.GetRequiredService<IImageServerLayerResolver>(),
            AuthorizationOperation.Metadata,
            request.SoapNamespace,
            cancellationToken).ConfigureAwait(false);
        return revalidation.ErrorResult is null
            ? (revalidation.Resolution.LayerId, null)
            : (default, revalidation.ErrorResult);
    }

    private static bool TryGetPayload<T>(IResult result, out T payload)
        where T : class
    {
        if (result is IValueHttpResult { Value: T value })
        {
            payload = value;
            return true;
        }

        payload = null!;
        return false;
    }

    private static XElement? DirectChild(XElement parent, string localName)
        => parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName);

    private static string? DirectChildText(XElement parent, string localName)
        => DirectChild(parent, localName)?.Value;

    private static bool IsNilElement(XElement element)
    {
        var nil = element.Attribute(XName.Get("nil", XmlSchemaInstanceNamespace));
        return nil is not null
            && (string.Equals(nil.Value, "true", StringComparison.OrdinalIgnoreCase) || nil.Value == "1");
    }

    private static XElement NilElement(string name)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        return new XElement(name, new XAttribute(xsi + "nil", true));
    }

    private static XElement NilValue()
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        return new XElement("Value", new XAttribute(xsi + "nil", true));
    }

    private static XElement TypedValue(string schemaType, string text)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        return new XElement("Value", new XAttribute(xsi + "type", "xsd:" + schemaType), text);
    }

    private static string FormatUnixTime(long unixMilliseconds)
        => XmlConvert.ToString(
            DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime,
            XmlDateTimeSerializationMode.Utc);
}
