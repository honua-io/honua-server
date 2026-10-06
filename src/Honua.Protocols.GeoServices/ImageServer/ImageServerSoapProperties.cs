// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
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
        var geometry = DirectChild(operation, "Geometry");
        if (geometry is not null && !IsNilElement(geometry))
        {
            return CreateSoapFault(
                "ComputeHistograms geometry filters are not supported; omit geometry for whole-service histograms.",
                StatusCodes.Status400BadRequest,
                request.SoapNamespace);
        }

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

        XNamespace xsi = XmlSchemaInstanceNamespace;
        return CreateSoapResponse(
            request.SoapNamespace,
            request.OperationNamespace,
            "ComputeHistogramsResponse",
            new XElement(
                "Result",
                new XAttribute(xsi + "type", "tns:ArrayOfRasterHistogram"),
                histograms.Histograms!.Select(BuildRasterHistogram)));
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
                    new XAttribute(xsi + "type", "tns:ArrayOfPropertySet"),
                    properties.BandProperties.Select(BuildBandPropertySet))),
        };
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

    private static XElement BuildBandPropertySet(BandProperty band)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        return new XElement(
            "PropertySet",
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
                (info?.Variables ?? []).Select(variable => new XElement(
                    "Variable",
                    new XElement("Name", variable.Name),
                    variable.Description is null ? null : new XElement("Description", variable.Description),
                    variable.Unit is null ? null : new XElement("Unit", variable.Unit),
                    new XElement(
                        "Dimensions",
                        variable.Dimensions.Select(dimension => new XElement(
                            "Dimension",
                            new XElement("Name", dimension.Name),
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
