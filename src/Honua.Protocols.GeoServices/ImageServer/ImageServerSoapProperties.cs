// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Microsoft.Extensions.Primitives;
using static Honua.Protocols.GeoServices.Soap.ArcGisSoapProtocol;

namespace Honua.Protocols.GeoServices.ImageServer;

internal static partial class ImageServerSoapEndpoints
{
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
        SoapRasterRequestContext request,
        string responseName,
        CancellationToken cancellationToken)
    {
        var revalidation = await RevalidateMetadataAsync(request, cancellationToken).ConfigureAwait(false);
        if (revalidation.Error is not null)
        {
            return revalidation.Error;
        }

        var handler = request.HttpContext.RequestServices.GetRequiredService<ImageServerKeyPropertiesHandler>();
        var result = await handler.GetKeyPropertiesAsync(
            request.HttpContext,
            revalidation.LayerId,
            cancellationToken).ConfigureAwait(false);
        if (!TryGetPayload<KeyPropertiesResponse>(result, out var properties))
        {
            return CreateSoapFaultFromResult(result, "Image key properties could not be read.", request.SoapNamespace);
        }

        return CreateSoapResponse(
            request.SoapNamespace,
            request.OperationNamespace,
            responseName,
            BuildKeyPropertiesResult(properties));
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
        error = null;
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
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("rasterFunction", name.Trim());
            if (arguments is not null && !IsNilElement(arguments))
            {
                writer.WritePropertyName("rasterFunctionArguments");
                writer.WriteStartObject();
                if (!TryWriteRenderingArguments(writer, arguments, out error))
                {
                    return false;
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        json = Encoding.UTF8.GetString(stream.ToArray());
        return true;
    }

    private static bool TryWriteRenderingArguments(Utf8JsonWriter writer, XElement arguments, out string? error)
    {
        error = null;
        var properties = arguments.Descendants()
            .Where(element => element.Name.LocalName == "PropertySetProperty")
            .ToArray();
        if (properties.Length > 0)
        {
            foreach (var property in properties)
            {
                var key = DirectChildText(property, "Key");
                var value = DirectChild(property, "Value");
                if (string.IsNullOrWhiteSpace(key) || value is null || value.HasElements)
                {
                    error = "RenderingRule argument properties require a scalar Key and Value.";
                    return false;
                }

                writer.WritePropertyName(key.Trim());
                WriteSoapScalar(writer, value);
            }

            return true;
        }

        foreach (var child in arguments.Elements())
        {
            if (child.HasElements)
            {
                error = $"RenderingRule argument '{child.Name.LocalName}' is not a scalar value.";
                return false;
            }

            writer.WritePropertyName(child.Name.LocalName);
            WriteSoapScalar(writer, child);
        }

        return true;
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
        XNamespace soapNamespace,
        CancellationToken cancellationToken)
    {
        var info = await ReadMultidimensionalInfoAsync(context, layerId, cancellationToken).ConfigureAwait(false);
        var histograms = await ReadServiceHistogramsAsync(
            context,
            layerId,
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

    private static XElement BuildKeyPropertiesResult(KeyPropertiesResponse properties)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        var items = new List<XElement>
        {
            new(
                "PropertySetProperty",
                new XAttribute(xsi + "type", "tns:PropertySetProperty"),
                new XElement("Key", "BandProperties"),
                new XElement(
                    "Value",
                    new XAttribute(xsi + "type", "tns:ArrayOfArgument"),
                    properties.BandProperties.Select(BuildBandPropertyArgument))),
        };
        AddDoubleProperty(items, "LowCellSize", properties.LowCellSize, xsi);
        AddDoubleProperty(items, "HighCellSize", properties.HighCellSize, xsi);
        AddDoubleProperty(items, "MaxCellSize", properties.MaxCellSize, xsi);
        items.Add(BuildProperty("ConfigKeyword", properties.ConfigKeyword, "xsd:string", xsi));
        items.Add(BuildProperty("BandDefinitionKeyword", properties.BandDefinitionKeyword, "xsd:string", xsi));
        if (properties.DataType is not null)
        {
            items.Add(BuildProperty("DataType", "Generic", "xsd:string", xsi));
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

    private static XElement BuildBandPropertyArgument(BandProperty band)
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

    // An unscanned layer is an empty variable list. A nil Result would disagree with REST,
    // which returns variables:[] for the same layer.
    private static XElement BuildMultidimensionalResult(ImageServerMultidimensionalInfo? info)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        return new XElement(
            "Result",
            new XAttribute(xsi + "type", "tns:MultidimensionalInfo"),
            new XElement(
                "Variables",
                new XAttribute(xsi + "type", "tns:ArrayOfMultidimensionalVariable"),
                (info?.Variables ?? []).Select(variable => new XElement(
                    "MultidimensionalVariable",
                    new XAttribute(xsi + "type", "tns:MultidimensionalVariable"),
                    new XElement("VariableName", variable.Name),
                    variable.Description is null ? null : new XElement("Description", variable.Description),
                    variable.Unit is null ? null : new XElement("Unit", variable.Unit),
                    new XElement(
                        "Dimensions",
                        new XAttribute(xsi + "type", "tns:ArrayOfMultidimensionalDimension"),
                        variable.Dimensions.Select(dimension => new XElement(
                            "MultidimensionalDimension",
                            new XAttribute(xsi + "type", "tns:MultidimensionalDimension"),
                            new XElement("DimensionName", dimension.Name),
                            dimension.Unit is null ? null : new XElement("Unit", dimension.Unit),
                            dimension.Extent is null ? null : BuildDoubleArray("Extent", dimension.Extent),
                            dimension.Values is null ? null : BuildDoubleArray("Values", dimension.Values),
                            new XElement("HasRegularIntervals", dimension.HasRegularIntervals),
                            new XElement("DimensionSize", dimension.DimensionSize))))))));
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
