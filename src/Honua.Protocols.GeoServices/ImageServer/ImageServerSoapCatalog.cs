// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Microsoft.Extensions.Primitives;
using static Honua.Protocols.GeoServices.Soap.ArcGisSoapProtocol;

namespace Honua.Protocols.GeoServices.ImageServer;

internal static partial class ImageServerSoapEndpoints
{
    private enum CatalogSoapResultKind
    {
        Count,
        Ids,
        Items,
    }

    // Shape is the footprint geometry column. It is not a catalog attribute, so it
    // selects returnGeometry and is removed before outFields reaches the query handler.
    private static async Task<IResult> HandleCatalogAsync(
        XElement operation,
        SoapRasterRequestContext request,
        CatalogSoapResultKind kind,
        CancellationToken cancellationToken)
    {
        var catalogName = DirectChildText(operation, "Name");
        if (catalogName is not null
            && !string.Equals(catalogName, "Catalog", StringComparison.Ordinal))
        {
            return CreateSoapFault(
                "ImageServer catalog operations read the Catalog raster catalog.",
                StatusCodes.Status400BadRequest,
                request.SoapNamespace);
        }

        if (DirectChild(operation, "QueryFilter") is not { } queryFilter)
        {
            return CreateSoapFault(
                "QueryFilter is required.",
                StatusCodes.Status400BadRequest,
                request.SoapNamespace);
        }

        if (!TryBuildCatalogQuery(queryFilter, kind, out var values, out var returnGeometry, out var error))
        {
            return CreateSoapFault(error!, StatusCodes.Status400BadRequest, request.SoapNamespace);
        }

        var revalidation = await RevalidateRasterPublicationAsync(
            request.Resolution,
            request.HttpContext,
            request.HttpContext.RequestServices.GetRequiredService<IImageServerLayerResolver>(),
            AuthorizationOperation.Query,
            request.SoapNamespace,
            cancellationToken).ConfigureAwait(false);
        if (revalidation.ErrorResult is not null)
        {
            return revalidation.ErrorResult;
        }

        var handler = request.HttpContext.RequestServices.GetRequiredService<ImageServerCatalogQueryHandler>();
        var result = await handler.QueryCatalogAsync(
            request.HttpContext,
            revalidation.Resolution.LayerId,
            values,
            cancellationToken).ConfigureAwait(false);

        var responseName = kind switch
        {
            CatalogSoapResultKind.Count => "GetCatalogItemCountResponse",
            CatalogSoapResultKind.Ids => "GetCatalogItemIDsResponse",
            _ => "GetCatalogItemsResponse",
        };
        var payload = kind switch
        {
            CatalogSoapResultKind.Count => BuildCatalogCount(result, request),
            CatalogSoapResultKind.Ids => BuildCatalogIds(result, request),
            _ => BuildCatalogItems(result, request, returnGeometry),
        };
        if (payload.Error is not null)
        {
            return payload.Error;
        }

        return CreateSoapResponse(
            request.SoapNamespace,
            request.OperationNamespace,
            responseName,
            payload.Result);
    }

    private static bool TryBuildCatalogQuery(
        XElement queryFilter,
        CatalogSoapResultKind kind,
        out Dictionary<string, StringValues> values,
        out bool returnGeometry,
        out string? error)
    {
        values = new Dictionary<string, StringValues>(StringComparer.Ordinal) { ["f"] = "json" };
        returnGeometry = true;
        error = null;

        var where = DirectChildText(queryFilter, "WhereClause");
        if (!string.IsNullOrWhiteSpace(where))
        {
            values["where"] = where;
        }

        var subFields = DirectChildText(queryFilter, "SubFields");
        if (!string.IsNullOrWhiteSpace(subFields) && subFields.Trim() != "*")
        {
            var parts = subFields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            returnGeometry = parts.Any(static part => string.Equals(part, "Shape", StringComparison.OrdinalIgnoreCase));
            var attributes = parts
                .Where(static part => !string.Equals(part, "Shape", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            values["outFields"] = attributes.Length == 0 ? "OBJECTID" : string.Join(',', attributes);
        }

        if (DirectChild(queryFilter, "OutputSpatialReference") is { } spatialReference)
        {
            var wkid = DirectChildText(spatialReference, "WKID");
            if (string.IsNullOrWhiteSpace(wkid))
            {
                wkid = DirectChildText(spatialReference, "LatestWKID");
            }

            if (string.IsNullOrWhiteSpace(wkid))
            {
                error = "OutputSpatialReference requires a WKID.";
                return false;
            }

            values["outSR"] = wkid;
        }

        values["returnGeometry"] = XmlConvert.ToString(returnGeometry);
        switch (kind)
        {
            case CatalogSoapResultKind.Count:
                values["returnCountOnly"] = "true";
                break;
            case CatalogSoapResultKind.Ids:
                values["returnIdsOnly"] = "true";
                break;
        }

        return true;
    }

    private static (XElement? Result, IResult? Error) BuildCatalogCount(IResult result, SoapRasterRequestContext request)
    {
        if (!TryGetPayload<CatalogCountResponse>(result, out var count))
        {
            return (null, CreateSoapFaultFromResult(result, "Image catalog query failed.", request.SoapNamespace));
        }

        if (count.Count is < int.MinValue or > int.MaxValue)
        {
            return (null, CreateSoapFault(
                "Catalog item count exceeds the SOAP integer range.",
                StatusCodes.Status500InternalServerError,
                request.SoapNamespace));
        }

        return (new XElement("Result", (int)count.Count), null);
    }

    private static (XElement? Result, IResult? Error) BuildCatalogIds(IResult result, SoapRasterRequestContext request)
    {
        if (!TryGetPayload<CatalogObjectIdsResponse>(result, out var ids))
        {
            return (null, CreateSoapFaultFromResult(result, "Image catalog query failed.", request.SoapNamespace));
        }

        var soapIds = new List<int>(ids.ObjectIds.Length);
        foreach (var objectId in ids.ObjectIds)
        {
            if (!TryToSoapInt(objectId, out var soapId))
            {
                return (null, CreateSoapFault(
                    "Catalog item id exceeds the SOAP integer range.",
                    StatusCodes.Status500InternalServerError,
                    request.SoapNamespace));
            }

            soapIds.Add(soapId);
        }

        XNamespace xsi = XmlSchemaInstanceNamespace;
        return (new XElement(
            "Result",
            new XAttribute(xsi + "type", "tns:FIDSet"),
            new XElement(
                "FIDArray",
                new XAttribute(xsi + "type", "tns:ArrayOfInt"),
                soapIds.Select(static id => new XElement("Int", id)))), null);
    }

    private static (XElement? Result, IResult? Error) BuildCatalogItems(
        IResult result,
        SoapRasterRequestContext request,
        bool returnGeometry)
    {
        if (!TryGetPayload<CatalogQueryResponse>(result, out var response))
        {
            return (null, CreateSoapFaultFromResult(result, "Image catalog query failed.", request.SoapNamespace));
        }

        var geometrySrid = response.Features
                .Select(static feature => feature.Geometry?.SpatialReference.Wkid)
                .FirstOrDefault(static wkid => wkid.HasValue)
            ?? response.SpatialReference.Wkid
            ?? 4326;
        var latestWkid = response.Features
                .Select(static feature => feature.Geometry?.SpatialReference.LatestWkid)
                .FirstOrDefault(static wkid => wkid.HasValue)
            ?? response.SpatialReference.LatestWkid
            ?? geometrySrid;

        XNamespace xsi = XmlSchemaInstanceNamespace;
        var fields = response.Fields.Select(static field => BuildCatalogField(field, geometryDef: null)).ToList();
        if (returnGeometry)
        {
            fields.Add(BuildCatalogField(
                new Field
                {
                    Name = "Shape",
                    Type = "esriFieldTypeGeometry",
                    Alias = "Shape",
                    Nullable = true,
                },
                BuildGeometryDef(geometrySrid, latestWkid)));
        }

        var records = new List<XElement>(response.Features.Length);
        foreach (var feature in response.Features)
        {
            var cells = new List<XElement>(fields.Count);
            foreach (var field in response.Fields)
            {
                feature.Attributes.TryGetValue(field.Name, out var attribute);
                if (!TryBuildAttributeValue(attribute, field.Type, field.Name, out var cell, out var valueError))
                {
                    return (null, CreateSoapFault(
                        valueError!,
                        StatusCodes.Status500InternalServerError,
                        request.SoapNamespace));
                }

                cells.Add(cell);
            }

            if (returnGeometry)
            {
                cells.Add(feature.Geometry is { Rings.Length: > 0 } geometry
                    ? BuildPolygonValue(geometry)
                    : NilValue());
            }

            records.Add(new XElement(
                "Record",
                new XAttribute(xsi + "type", "tns:Record"),
                new XElement(
                    "Values",
                    new XAttribute(xsi + "type", "tns:ArrayOfValue"),
                    cells)));
        }

        return (new XElement(
            "Result",
            new XAttribute(xsi + "type", "tns:RecordSet"),
            new XElement(
                "Fields",
                new XAttribute(xsi + "type", "tns:Fields"),
                new XElement(
                    "FieldArray",
                    new XAttribute(xsi + "type", "tns:ArrayOfField"),
                    fields)),
            new XElement(
                "Records",
                new XAttribute(xsi + "type", "tns:ArrayOfRecord"),
                records)), null);
    }

    private static XElement BuildCatalogField(Field field, XElement? geometryDef)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        var length = field.Type switch
        {
            "esriFieldTypeOID" or "esriFieldTypeInteger" or "esriFieldTypeSingle" => 4,
            "esriFieldTypeDouble" or "esriFieldTypeDate" => 8,
            "esriFieldTypeString" => 255,
            _ => 0,
        };
        return new XElement(
            "Field",
            new XAttribute(xsi + "type", "tns:Field"),
            new XElement("Name", field.Name),
            new XElement("Type", field.Type),
            new XElement("IsNullable", field.Nullable || geometryDef is not null),
            new XElement("Length", length),
            new XElement("Precision", 0),
            new XElement("Scale", 0),
            new XElement("Required", geometryDef is not null || !field.Nullable),
            new XElement("Editable", false),
            geometryDef,
            new XElement("AliasName", field.Alias ?? field.Name),
            new XElement("ModelName", field.Name));
    }

    private static XElement BuildGeometryDef(int wkid, int latestWkid)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        return new XElement(
            "GeometryDef",
            new XAttribute(xsi + "type", "tns:GeometryDef"),
            new XElement("AvgNumPoints", 0),
            new XElement("GeometryType", "esriGeometryPolygon"),
            new XElement("HasM", false),
            new XElement("HasZ", false),
            new XElement(
                "SpatialReference",
                new XAttribute(
                    xsi + "type",
                    GeographicSridClassifier.IsGeographicSrid(wkid)
                        ? "tns:GeographicCoordinateSystem"
                        : "tns:ProjectedCoordinateSystem"),
                new XElement("WKID", wkid),
                new XElement("LatestWKID", latestWkid)));
    }

    private static bool TryBuildAttributeValue(
        object? value,
        string fieldType,
        string fieldName,
        out XElement cell,
        out string? error)
    {
        cell = NilValue();
        error = null;
        if (value is null)
        {
            return true;
        }

        switch (fieldType)
        {
            case "esriFieldTypeOID" or "esriFieldTypeInteger" or "esriFieldTypeSmallInteger":
                if (!TryCoerceSoapInt(value, out var integer))
                {
                    error = $"Catalog field '{fieldName}' exceeds the SOAP integer range.";
                    return false;
                }

                cell = TypedValue("int", XmlConvert.ToString(integer));
                return true;
            case "esriFieldTypeDouble" or "esriFieldTypeSingle":
                if (!TryCoerceDouble(value, out var number))
                {
                    error = $"Catalog field '{fieldName}' is not a numeric value.";
                    return false;
                }

                cell = TypedValue("double", FormatDouble(number));
                return true;
            case "esriFieldTypeString":
                cell = TypedValue("string", value as string ?? value.ToString() ?? string.Empty);
                return true;
            case "esriFieldTypeDate" when TryCoerceEpochMilliseconds(value, out var epoch):
                cell = TypedValue("dateTime", FormatUnixTime(epoch));
                return true;
            case "esriFieldTypeDate" when value is DateTimeOffset date:
                cell = TypedValue(
                    "dateTime",
                    XmlConvert.ToString(date.UtcDateTime, XmlDateTimeSerializationMode.Utc));
                return true;
            default:
                error = $"Catalog field '{fieldName}' cannot be encoded as SOAP type '{fieldType}'.";
                return false;
        }
    }

    private static XElement BuildPolygonValue(CatalogQueryGeometry geometry)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        var points = geometry.Rings.SelectMany(static ring => ring).Where(static point => point.Length >= 2).ToArray();
        return new XElement(
            "Value",
            new XAttribute(xsi + "type", "tns:PolygonN"),
            new XElement("HasID", false),
            new XElement("HasZ", false),
            new XElement("HasM", false),
            points.Length == 0
                ? null
                : new XElement(
                    "Extent",
                    new XAttribute(xsi + "type", "tns:EnvelopeN"),
                    new XElement("XMin", FormatDouble(points.Min(static point => point[0]))),
                    new XElement("YMin", FormatDouble(points.Min(static point => point[1]))),
                    new XElement("XMax", FormatDouble(points.Max(static point => point[0]))),
                    new XElement("YMax", FormatDouble(points.Max(static point => point[1])))),
            new XElement(
                "RingArray",
                new XAttribute(xsi + "type", "tns:ArrayOfRing"),
                geometry.Rings.Select(ring => new XElement(
                    "Ring",
                    new XAttribute(xsi + "type", "tns:Ring"),
                    new XElement(
                        "PointArray",
                        new XAttribute(xsi + "type", "tns:ArrayOfPoint"),
                        ring.Where(static point => point.Length >= 2).Select(point => new XElement(
                            "Point",
                            new XAttribute(xsi + "type", "tns:PointN"),
                            new XElement("X", FormatDouble(point[0])),
                            new XElement("Y", FormatDouble(point[1])))))))));
    }

    private static bool TryCoerceSoapInt(object value, out int soapInt)
    {
        switch (value)
        {
            case int integer:
                soapInt = integer;
                return true;
            case long integer when TryToSoapInt(integer, out soapInt):
                return true;
            case short integer:
                soapInt = integer;
                return true;
            default:
                soapInt = 0;
                return false;
        }
    }

    private static bool TryToSoapInt(long value, out int soapInt)
    {
        if (value is < int.MinValue or > int.MaxValue)
        {
            soapInt = 0;
            return false;
        }

        soapInt = (int)value;
        return true;
    }

    private static bool TryCoerceDouble(object value, out double number)
    {
        switch (value)
        {
            case double parsed:
                number = parsed;
                return true;
            case float parsed:
                number = parsed;
                return true;
            case int parsed:
                number = parsed;
                return true;
            case long parsed:
                number = parsed;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    private static bool TryCoerceEpochMilliseconds(object value, out long epoch)
    {
        switch (value)
        {
            case long parsed:
                epoch = parsed;
                return true;
            case int parsed:
                epoch = parsed;
                return true;
            default:
                epoch = 0;
                return false;
        }
    }
}
