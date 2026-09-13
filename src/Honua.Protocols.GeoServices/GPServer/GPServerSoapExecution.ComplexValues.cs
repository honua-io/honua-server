// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices.GPServer.Models;

namespace Honua.Protocols.GeoServices.GPServer;

/// <summary>
/// Complex SOAP GPValues (Esri RecordSet, GPMultiValue and GDSData references). Inputs become
/// the Esri JSON values the REST adapter already accepts, and outputs re-encode the REST result
/// value, so validation, authorization and execution stay in the shared GP handlers.
/// </summary>
internal static partial class GPServerSoapExecution
{
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    private static readonly string[] SpatialReferenceElements =
    [
        "WKT", "XOrigin", "YOrigin", "XYScale", "ZOrigin", "ZScale", "MOrigin", "MScale", "XYTolerance",
        "ZTolerance", "MTolerance", "HighPrecision", "LeftLongitude", "WKID", "LatestWKID", "VCSWKID", "LatestVCSWKID"
    ];

    private static bool IsScalarType(string? type)
        => type is "GPString" or "GPLong" or "GPDouble" or "GPBoolean" or "GPDate";

    private static string ReadInputValue(XElement value, string type, GPParameterInfo input)
        => type switch
        {
            "GPMultiValue" => ReadMultiValue(value, input),
            "GPFeatureRecordSetLayer" or "GPRecordSet" => ReadRecordSet(value, type),
            _ => ReadValue(value, type)
        };

    // REST accepts a GPMultiValue as a JSON array; the canonical translation validates it.
    private static string ReadMultiValue(XElement value, GPParameterInfo input)
    {
        ValidateChildren(value, "MemberDataType", "Values");
        var memberType = input.DataType!["GPMultiValue:".Length..];
        if (!IsScalarType(memberType))
        {
            throw Invalid($"Input '{input.Name}' has an unsupported SOAP multivalue member type.");
        }
        var declared = value.Element("MemberDataType");
        if (declared is not null && (declared.HasElements || declared.Value != memberType))
        {
            throw Invalid($"Input '{input.Name}' requires multivalue members of type '{memberType}'.");
        }
        var members = value.Element("Values") ?? throw Invalid($"Input '{input.Name}' requires multivalue Values.");
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var member in ArrayItems(members, "GPValue"))
            {
                if (IsNil(member) || ResolveType(member) != memberType)
                {
                    throw Invalid($"Input '{input.Name}' requires non-null multivalue members of type '{memberType}'.");
                }
                writer.WriteStringValue(ReadValue(member, memberType));
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // An Esri RecordSet becomes the Esri JSON FeatureSet a REST client submits for the same input.
    private static string ReadRecordSet(XElement value, string type)
    {
        if (type == "GPRecordSet")
        {
            ValidateChildren(value, "RecordSet", "OIDFieldName", "ExceededTransferLimit");
        }
        else
        {
            ValidateChildren(value, "RecordSet", "OIDFieldName", "ShapeFieldName", "ExceededTransferLimit");
        }
        if (value.Element("ExceededTransferLimit") is { } exceeded && XmlBoolean(exceeded))
        {
            throw Invalid("A truncated RecordSet input (ExceededTransferLimit) cannot be executed.");
        }
        var recordSet = value.Element("RecordSet") ?? throw Invalid("A RecordSet GPValue requires RecordSet.");
        ValidateChildren(recordSet, "Fields", "Records");
        var fieldsElement = recordSet.Element("Fields") ?? throw Invalid("A RecordSet requires Fields.");
        ValidateChildren(fieldsElement, "FieldArray");
        var fields = ArrayItems(fieldsElement.Element("FieldArray") ?? throw Invalid("A RecordSet requires FieldArray."), "Field")
            .Select(ReadField).ToArray();
        if (fields.Select(field => field.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Length)
        {
            throw Invalid("RecordSet field names must be unique.");
        }
        var shapes = fields.Where(field => field.Type == "esriFieldTypeGeometry").ToArray();
        if (shapes.Length > 1 || (type == "GPRecordSet" ? shapes.Length != 0 : shapes.Length != 1))
        {
            throw Invalid(type == "GPRecordSet"
                ? "A GPRecordSet input cannot contain a geometry field."
                : "A GPFeatureRecordSetLayer input requires exactly one geometry field.");
        }
        foreach (var reference in new[] { "OIDFieldName", "ShapeFieldName" })
        {
            if (value.Element(reference) is { } named && !fields.Any(field => field.Name == named.Value))
            {
                throw Invalid($"{reference} must name a RecordSet field.");
            }
        }
        var shape = shapes.SingleOrDefault();

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (shape is not null)
            {
                writer.WriteString("geometryType", shape.GeometryType);
                writer.WriteBoolean("hasZ", shape.HasZ);
                writer.WriteBoolean("hasM", shape.HasM);
                // ArcPy sends Esri codes with their EPSG successor (102100 with 3857).
                writer.WriteStartObject("spatialReference");
                writer.WriteNumber("wkid", shape.LatestWkid ?? shape.Wkid!.Value);
                writer.WriteEndObject();
            }
            writer.WriteStartArray("fields");
            foreach (var field in fields.Where(field => field != shape))
            {
                writer.WriteStartObject();
                writer.WriteString("name", field.Name);
                writer.WriteString("type", field.Type);
                if (field.Alias is not null)
                {
                    writer.WriteString("alias", field.Alias);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("features");
            var recordsElement = recordSet.Element("Records") ?? throw Invalid("A RecordSet requires Records.");
            foreach (var record in ArrayItems(recordsElement, "Record"))
            {
                ValidateChildren(record, "Values");
                var values = ArrayItems(record.Element("Values") ?? throw Invalid("A Record requires Values."), "Value").ToArray();
                if (values.Length != fields.Length)
                {
                    throw Invalid("Each RecordSet record requires one value per field.");
                }
                writer.WriteStartObject();
                writer.WriteStartObject("attributes");
                for (var index = 0; index < fields.Length; index++)
                {
                    if (fields[index] != shape)
                    {
                        writer.WritePropertyName(fields[index].Name);
                        WriteAttribute(writer, values[index], fields[index].Type);
                    }
                }
                writer.WriteEndObject();
                if (shape is not null)
                {
                    writer.WritePropertyName("geometry");
                    var geometry = values[Array.IndexOf(fields, shape)];
                    if (IsNil(geometry))
                    {
                        writer.WriteNullValue();
                    }
                    else
                    {
                        WriteGeometry(writer, geometry, shape);
                    }
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static SoapField ReadField(XElement field)
    {
        ValidateChildren(field, "Name", "Type", "IsNullable", "Length", "Precision", "Scale", "Required", "Editable",
            "DomainFixed", "GeometryDef", "AliasName", "ModelName");
        var name = RequiredScalar(field, "Name");
        var type = RequiredScalar(field, "Type");
        var alias = field.Element("AliasName") is { HasElements: false } aliasElement ? aliasElement.Value : null;
        if (type is not ("esriFieldTypeOID" or "esriFieldTypeInteger" or "esriFieldTypeSmallInteger" or "esriFieldTypeDouble"
            or "esriFieldTypeSingle" or "esriFieldTypeString" or "esriFieldTypeDate" or "esriFieldTypeGUID"
            or "esriFieldTypeGlobalID" or "esriFieldTypeGeometry"))
        {
            throw Invalid($"RecordSet field '{name}' has unsupported type '{type}'.");
        }
        var definition = field.Element("GeometryDef");
        if (type != "esriFieldTypeGeometry")
        {
            return definition is null
                ? new SoapField(name, type, alias, null, false, false, null, null)
                : throw Invalid($"RecordSet field '{name}' is not a geometry field.");
        }
        if (definition is null)
        {
            throw Invalid($"Geometry field '{name}' requires GeometryDef.");
        }
        ValidateChildren(definition, "AvgNumPoints", "GeometryType", "HasM", "HasZ", "SpatialReference",
            "GridSize0", "GridSize1", "GridSize2");
        var geometryType = RequiredScalar(definition, "GeometryType");
        if (geometryType is not ("esriGeometryPoint" or "esriGeometryMultipoint" or "esriGeometryPolyline" or "esriGeometryPolygon"))
        {
            throw Invalid($"Geometry field '{name}' has unsupported geometry type '{geometryType}'.");
        }
        var spatialReference = definition.Element("SpatialReference")
            ?? throw Invalid($"Geometry field '{name}' requires a spatial reference.");
        ValidateChildren(spatialReference, SpatialReferenceElements);
        if (spatialReference.Element("VCSWKID") is not null || spatialReference.Element("LatestVCSWKID") is not null)
        {
            throw Invalid("RecordSet vertical coordinate systems are not supported.");
        }
        var wkid = OptionalInt(spatialReference, "WKID");
        var latest = OptionalInt(spatialReference, "LatestWKID");
        if ((wkid ?? latest) is not { } resolved || resolved <= 0)
        {
            throw Invalid("RecordSet spatial references require a WKID.");
        }
        return new SoapField(name, type, alias, geometryType,
            XmlBoolean(RequiredElement(definition, "HasZ")), XmlBoolean(RequiredElement(definition, "HasM")),
            wkid ?? latest, latest != wkid ? latest : null);
    }

    private static void WriteAttribute(Utf8JsonWriter writer, XElement value, string fieldType)
    {
        if (IsNil(value))
        {
            writer.WriteNullValue();
            return;
        }
        if (value.HasElements)
        {
            throw Invalid("RecordSet attribute values must be scalar.");
        }
        var schemaType = ResolveSchemaType(value);
        var text = value.Value;
        switch (fieldType)
        {
            case "esriFieldTypeOID" or "esriFieldTypeInteger" or "esriFieldTypeSmallInteger"
                when schemaType is "int" or "short" or "long":
                writer.WriteNumberValue(XmlConvert.ToInt64(text));
                break;
            case "esriFieldTypeDouble" or "esriFieldTypeSingle" when schemaType is "double" or "float":
                writer.WriteNumberValue(Finite(XmlConvert.ToDouble(text)));
                break;
            case "esriFieldTypeString" or "esriFieldTypeGUID" or "esriFieldTypeGlobalID" when schemaType == "string":
                writer.WriteStringValue(text);
                break;
            case "esriFieldTypeDate" when schemaType == "dateTime":
                // Esri JSON dates are epoch milliseconds, as accepted on the REST path.
                writer.WriteNumberValue(XmlConvert.ToDateTimeOffset(text).ToUnixTimeMilliseconds());
                break;
            default:
                throw Invalid($"RecordSet value type '{schemaType}' does not match field type '{fieldType}'.");
        }
    }

    private static void WriteGeometry(Utf8JsonWriter writer, XElement value, SoapField shape)
    {
        var type = ResolveType(value);
        var expected = shape.GeometryType switch
        {
            "esriGeometryPoint" => "PointN",
            "esriGeometryMultipoint" => "MultipointN",
            "esriGeometryPolyline" => "PolylineN",
            _ => "PolygonN"
        };
        if (type != expected)
        {
            throw Invalid($"RecordSet geometry '{type}' does not match '{shape.GeometryType}'.");
        }
        writer.WriteStartObject();
        if (type == "PointN")
        {
            var coordinates = ReadPoint(value, shape);
            writer.WriteNumber("x", coordinates[0]);
            writer.WriteNumber("y", coordinates[1]);
            if (shape.HasZ)
            {
                writer.WriteNumber("z", coordinates[2]);
            }
            if (shape.HasM)
            {
                writer.WriteNumber("m", coordinates[^1]);
            }
        }
        else
        {
            ValidateChildren(value, type == "MultipointN"
                ? ["HasID", "HasZ", "HasM", "Extent", "PointArray", "SpatialReference"]
                : ["HasID", "HasZ", "HasM", "Extent", type == "PolylineN" ? "PathArray" : "RingArray", "SpatialReference", "KnownSimple"]);
            ValidateGeometryFlags(value, shape);
            if (type == "MultipointN")
            {
                writer.WritePropertyName("points");
                WritePointArray(writer, value.Element("PointArray"), shape);
            }
            else
            {
                var (arrayName, partName, jsonName) = type == "PolylineN"
                    ? ("PathArray", "Path", "paths")
                    : ("RingArray", "Ring", "rings");
                writer.WriteStartArray(jsonName);
                foreach (var part in ArrayItems(value.Element(arrayName) ?? throw Invalid($"{type} requires {arrayName}."), partName))
                {
                    if (ResolveType(part) != partName)
                    {
                        throw Invalid($"{arrayName} entries must be {partName} values.");
                    }
                    if (part.Element("SegmentArray") is not null)
                    {
                        throw Invalid("RecordSet curve segments are not supported; densify the geometry before submission.");
                    }
                    ValidateChildren(part, "PointArray");
                    WritePointArray(writer, part.Element("PointArray"), shape);
                }
                writer.WriteEndArray();
            }
        }
        writer.WriteEndObject();
    }

    private static void ValidateGeometryFlags(XElement geometry, SoapField shape)
    {
        if (geometry.Element("HasID") is { } hasId && XmlBoolean(hasId))
        {
            throw Invalid("RecordSet point identifiers are not supported.");
        }
        if (XmlBoolean(RequiredElement(geometry, "HasZ")) != shape.HasZ || XmlBoolean(RequiredElement(geometry, "HasM")) != shape.HasM)
        {
            throw Invalid("RecordSet geometry Z/M flags must match the geometry field definition.");
        }
        ValidateGeometrySpatialReference(geometry, shape);
    }

    private static void ValidateGeometrySpatialReference(XElement geometry, SoapField shape)
    {
        if (geometry.Element("SpatialReference") is not { } spatialReference)
        {
            return;
        }
        ValidateChildren(spatialReference, SpatialReferenceElements);
        var wkid = OptionalInt(spatialReference, "WKID") ?? OptionalInt(spatialReference, "LatestWKID");
        if (wkid != shape.Wkid && wkid != shape.LatestWkid)
        {
            throw Invalid("RecordSet geometries must use the geometry field spatial reference.");
        }
    }

    private static void WritePointArray(Utf8JsonWriter writer, XElement? points, SoapField shape)
    {
        writer.WriteStartArray();
        if (points is not null)
        {
            foreach (var point in ArrayItems(points, "Point"))
            {
                if (ResolveType(point) != "PointN")
                {
                    throw Invalid("RecordSet point arrays must contain PointN values.");
                }
                writer.WriteStartArray();
                foreach (var ordinate in ReadPoint(point, shape))
                {
                    writer.WriteNumberValue(ordinate);
                }
                writer.WriteEndArray();
            }
        }
        writer.WriteEndArray();
    }

    // Esri JSON ordinate order: x, y, z (when hasZ), m (when hasM).
    private static double[] ReadPoint(XElement point, SoapField shape)
    {
        ValidateChildren(point, "X", "Y", "M", "Z", "ID", "SpatialReference");
        if (point.Element("ID") is not null)
        {
            throw Invalid("RecordSet point identifiers are not supported.");
        }
        if ((point.Element("Z") is not null) != shape.HasZ || (point.Element("M") is not null) != shape.HasM)
        {
            throw Invalid("RecordSet point ordinates must match the geometry field Z/M definition.");
        }
        ValidateGeometrySpatialReference(point, shape);
        var ordinates = new List<double>(4)
        {
            Finite(XmlConvert.ToDouble(RequiredScalar(point, "X"))),
            Finite(XmlConvert.ToDouble(RequiredScalar(point, "Y")))
        };
        if (shape.HasZ)
        {
            ordinates.Add(Finite(XmlConvert.ToDouble(RequiredScalar(point, "Z"))));
        }
        if (shape.HasM)
        {
            ordinates.Add(Finite(XmlConvert.ToDouble(RequiredScalar(point, "M"))));
        }
        return [.. ordinates];
    }

    private static XElement BuildComplexOutput(GPResultResponse output)
        => output.DataType switch
        {
            "GPFeatureRecordSetLayer" or "GPRecordSet" => BuildRecordSetOutput(output),
            "GPRasterDataLayer" or "GPRasterData" or "GPDataFile" => BuildDataReferenceOutput(output),
            _ => throw Invalid($"SOAP output type '{output.DataType}' is not supported.")
        };

    // Esri GDSData carries a URL for the URL transport, the only result transport accepted.
    private static XElement BuildDataReferenceOutput(GPResultResponse output)
    {
        if (output.Value is not JsonElement { ValueKind: JsonValueKind.Object } reference ||
            !reference.TryGetProperty("url", out var urlValue) || urlValue.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(urlValue.GetString(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            throw Invalid($"Output '{output.ParamName}' has no retrievable URL for the SOAP URL transport.");
        }
        return new XElement("GPValue", new XAttribute(Xsi + "type", "tns:" + output.DataType),
            new XElement("Data", new XAttribute(Xsi + "type", "tns:GDSData"),
                new XElement("Compressed", "false"),
                new XElement("TransportType", "esriGDSTransportTypeUrl"),
                new XElement("URL", url.OriginalString)));
    }

    private static XElement BuildRecordSetOutput(GPResultResponse output)
    {
        if (output.Value is not JsonElement { ValueKind: JsonValueKind.Object } set ||
            !set.TryGetProperty("fields", out var fieldsJson) || fieldsJson.ValueKind != JsonValueKind.Array ||
            !set.TryGetProperty("features", out var featuresJson) || featuresJson.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"Output '{output.ParamName}' is not an inline FeatureSet and cannot be encoded as a SOAP RecordSet.");
        }
        var fields = fieldsJson.EnumerateArray().Select(field => (
            Name: field.GetProperty("name").GetString()!,
            Type: field.GetProperty("type").GetString()!,
            Alias: field.TryGetProperty("alias", out var alias) && alias.ValueKind == JsonValueKind.String ? alias.GetString() : null,
            Length: field.TryGetProperty("length", out var length) && length.ValueKind == JsonValueKind.Number ? length.GetInt32() : (int?)null)).ToArray();
        var features = featuresJson.EnumerateArray().ToArray();
        var geometryType = output.DataType == "GPFeatureRecordSetLayer" &&
            set.TryGetProperty("geometryType", out var geometryTypeJson) ? geometryTypeJson.GetString() : null;
        var hasZ = set.TryGetProperty("hasZ", out var z) && z.ValueKind == JsonValueKind.True;
        var hasM = set.TryGetProperty("hasM", out var m) && m.ValueKind == JsonValueKind.True;
        var oidName = set.TryGetProperty("objectIdFieldName", out var oid) ? oid.GetString() : null;

        var fieldElements = new List<XElement>();
        foreach (var field in fields)
        {
            var oidField = field.Type == "esriFieldTypeOID";
            fieldElements.Add(new XElement("Field", new XAttribute(Xsi + "type", "tns:Field"),
                new XElement("Name", field.Name),
                new XElement("Type", field.Type),
                new XElement("IsNullable", oidField ? "false" : "true"),
                new XElement("Length", field.Length ?? DefaultFieldLength(field.Type, field.Name, features)),
                new XElement("Precision", 0),
                new XElement("Scale", 0),
                oidField ? new XElement("Required", "true") : null,
                oidField ? new XElement("Editable", "false") : null,
                new XElement("AliasName", field.Alias ?? field.Name)));
        }
        string? shapeName = null;
        GeometryEncoding? encoding = null;
        if (geometryType is not null)
        {
            var wkid = ReadWkid(set) ?? throw Invalid($"Output '{output.ParamName}' has no spatial reference WKID.");
            encoding = new GeometryEncoding(geometryType, hasZ, hasM);
            shapeName = "Shape";
            while (fields.Any(field => string.Equals(field.Name, shapeName, StringComparison.OrdinalIgnoreCase)))
            {
                shapeName = "_" + shapeName;
            }
            fieldElements.Add(new XElement("Field", new XAttribute(Xsi + "type", "tns:Field"),
                new XElement("Name", shapeName),
                new XElement("Type", "esriFieldTypeGeometry"),
                new XElement("IsNullable", "true"),
                new XElement("Length", 0),
                new XElement("Precision", 0),
                new XElement("Scale", 0),
                new XElement("Required", "true"),
                new XElement("GeometryDef", new XAttribute(Xsi + "type", "tns:GeometryDef"),
                    new XElement("AvgNumPoints", 0),
                    new XElement("GeometryType", geometryType),
                    new XElement("HasM", XmlConvert.ToString(hasM)),
                    new XElement("HasZ", XmlConvert.ToString(hasZ)),
                    new XElement("SpatialReference",
                        new XAttribute(Xsi + "type", GeographicSridClassifier.IsGeographicSrid(wkid)
                            ? "tns:GeographicCoordinateSystem" : "tns:ProjectedCoordinateSystem"),
                        new XElement("WKID", wkid))),
                new XElement("AliasName", shapeName)));
        }

        var records = features.Select(feature =>
        {
            var attributes = feature.TryGetProperty("attributes", out var values) && values.ValueKind == JsonValueKind.Object
                ? values : default;
            var cells = fields.Select(field => BuildAttributeValue(
                attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty(field.Name, out var cell) ? cell : default,
                field.Type, field.Name)).ToList();
            if (encoding is not null)
            {
                cells.Add(feature.TryGetProperty("geometry", out var geometry) && geometry.ValueKind == JsonValueKind.Object
                    ? BuildGeometryValue(geometry, encoding)
                    : Nil());
            }
            return new XElement("Record", new XAttribute(Xsi + "type", "tns:Record"),
                new XElement("Values", new XAttribute(Xsi + "type", "tns:ArrayOfValue"), cells));
        });

        return new XElement("GPValue", new XAttribute(Xsi + "type", "tns:" + output.DataType),
            new XElement("RecordSet", new XAttribute(Xsi + "type", "tns:RecordSet"),
                new XElement("Fields", new XAttribute(Xsi + "type", "tns:Fields"),
                    new XElement("FieldArray", new XAttribute(Xsi + "type", "tns:ArrayOfField"), fieldElements)),
                new XElement("Records", new XAttribute(Xsi + "type", "tns:ArrayOfRecord"), records)),
            oidName is null ? null : new XElement("OIDFieldName", oidName),
            shapeName is null ? null : new XElement("ShapeFieldName", shapeName));
    }

    private static int? ReadWkid(JsonElement set)
    {
        if (!set.TryGetProperty("spatialReference", out var spatialReference) || spatialReference.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var name in new[] { "wkid", "latestWkid" })
        {
            if (spatialReference.TryGetProperty(name, out var wkid) && wkid.TryGetInt32(out var value) && value > 0)
            {
                return value;
            }
        }
        return null;
    }

    private static int DefaultFieldLength(string type, string name, JsonElement[] features)
        => type switch
        {
            "esriFieldTypeSmallInteger" => 2,
            "esriFieldTypeOID" or "esriFieldTypeInteger" or "esriFieldTypeSingle" => 4,
            "esriFieldTypeDouble" or "esriFieldTypeDate" => 8,
            "esriFieldTypeGUID" or "esriFieldTypeGlobalID" => 38,
            // Esri string fields declare a maximum length; never declare one shorter than a value.
            _ => Math.Max(255, features.Max(feature =>
                feature.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object &&
                attributes.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
                    ? (value.ValueKind == JsonValueKind.String ? value.GetString()!.Length : value.GetRawText().Length)
                    : (int?)0) ?? 0)
        };

    private static XElement BuildAttributeValue(JsonElement value, string fieldType, string fieldName)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return Nil();
        }
        var (schemaType, text) = fieldType switch
        {
            "esriFieldTypeOID" or "esriFieldTypeInteger" when value.TryGetInt32(out var integer)
                => ("int", XmlConvert.ToString(integer)),
            "esriFieldTypeSmallInteger" when value.TryGetInt16(out var small)
                => ("short", XmlConvert.ToString(small)),
            "esriFieldTypeDouble" when value.ValueKind == JsonValueKind.Number
                => ("double", XmlConvert.ToString(value.GetDouble())),
            "esriFieldTypeSingle" when value.ValueKind == JsonValueKind.Number
                => ("float", XmlConvert.ToString((float)value.GetDouble())),
            "esriFieldTypeDate" when value.TryGetInt64(out var epoch)
                => ("dateTime", XmlConvert.ToString(DateTimeOffset.FromUnixTimeMilliseconds(epoch))),
            "esriFieldTypeDate" when value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
                => ("dateTime", XmlConvert.ToString(date)),
            "esriFieldTypeString" or "esriFieldTypeGUID" or "esriFieldTypeGlobalID"
                => ("string", value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()),
            _ => throw Invalid($"Output field '{fieldName}' value cannot be encoded as SOAP type '{fieldType}'.")
        };
        return new XElement("Value", new XAttribute(Xsi + "type", "xsd:" + schemaType), text);
    }

    private static XElement BuildGeometryValue(JsonElement geometry, GeometryEncoding encoding)
    {
        if (geometry.TryGetProperty("curvePaths", out _) || geometry.TryGetProperty("curveRings", out _))
        {
            throw Invalid("Curve geometries cannot be encoded as SOAP RecordSet values.");
        }
        var hasZ = encoding.HasZ;
        var hasM = encoding.HasM;
        if (encoding.GeometryType == "esriGeometryPoint")
        {
            if (!geometry.TryGetProperty("x", out var x) || x.ValueKind != JsonValueKind.Number)
            {
                return Nil();
            }
            return new XElement("Value", new XAttribute(Xsi + "type", "tns:PointN"),
                new XElement("X", XmlConvert.ToString(x.GetDouble())),
                new XElement("Y", XmlConvert.ToString(geometry.GetProperty("y").GetDouble())),
                hasM ? new XElement("M", XmlConvert.ToString(geometry.GetProperty("m").GetDouble())) : null,
                hasZ ? new XElement("Z", XmlConvert.ToString(geometry.GetProperty("z").GetDouble())) : null);
        }
        var (soapType, jsonName, arrayName, arrayType, partName, partType) = encoding.GeometryType switch
        {
            "esriGeometryMultipoint" => ("MultipointN", "points", "PointArray", "ArrayOfPoint", (string?)null, (string?)null),
            "esriGeometryPolyline" => ("PolylineN", "paths", "PathArray", "ArrayOfPath", "Path", "Path"),
            "esriGeometryPolygon" => ("PolygonN", "rings", "RingArray", "ArrayOfRing", "Ring", "Ring"),
            _ => throw Invalid($"Geometry type '{encoding.GeometryType}' cannot be encoded as a SOAP RecordSet value.")
        };
        var coordinates = geometry.GetProperty(jsonName);
        var allPoints = partName is null
            ? coordinates.EnumerateArray().ToArray()
            : coordinates.EnumerateArray().SelectMany(part => part.EnumerateArray()).ToArray();
        XElement BuildPoints(IEnumerable<JsonElement> points)
            => new("PointArray", new XAttribute(Xsi + "type", "tns:ArrayOfPoint"),
                points.Select(point => BuildPoint(point, hasZ, hasM)));
        return new XElement("Value", new XAttribute(Xsi + "type", "tns:" + soapType),
            new XElement("HasID", "false"),
            new XElement("HasZ", XmlConvert.ToString(hasZ)),
            new XElement("HasM", XmlConvert.ToString(hasM)),
            allPoints.Length == 0 ? null : new XElement("Extent", new XAttribute(Xsi + "type", "tns:EnvelopeN"),
                new XElement("XMin", XmlConvert.ToString(allPoints.Min(point => point[0].GetDouble()))),
                new XElement("YMin", XmlConvert.ToString(allPoints.Min(point => point[1].GetDouble()))),
                new XElement("XMax", XmlConvert.ToString(allPoints.Max(point => point[0].GetDouble()))),
                new XElement("YMax", XmlConvert.ToString(allPoints.Max(point => point[1].GetDouble())))),
            partName is null
                ? BuildPoints(coordinates.EnumerateArray())
                : new XElement(arrayName, new XAttribute(Xsi + "type", "tns:" + arrayType),
                    coordinates.EnumerateArray().Select(part => new XElement(partName,
                        new XAttribute(Xsi + "type", "tns:" + partType), BuildPoints(part.EnumerateArray())))));
    }

    // Esri JSON ordinate order is x, y, z (when hasZ), m (when hasM); PointN orders M before Z.
    private static XElement BuildPoint(JsonElement point, bool hasZ, bool hasM)
    {
        var expected = 2 + (hasZ ? 1 : 0) + (hasM ? 1 : 0);
        if (point.GetArrayLength() != expected)
        {
            throw Invalid("Output coordinates do not match the FeatureSet Z/M flags.");
        }
        return new XElement("Point", new XAttribute(Xsi + "type", "tns:PointN"),
            new XElement("X", XmlConvert.ToString(point[0].GetDouble())),
            new XElement("Y", XmlConvert.ToString(point[1].GetDouble())),
            hasM ? new XElement("M", XmlConvert.ToString(point[expected - 1].GetDouble())) : null,
            hasZ ? new XElement("Z", XmlConvert.ToString(point[2].GetDouble())) : null);
    }

    private static XElement Nil() => new("Value", new XAttribute(Xsi + "nil", "true"));

    private static IEnumerable<XElement> ArrayItems(XElement array, string itemName)
    {
        if (array.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)) ||
            array.Elements().Any(item => item.Name != XName.Get(itemName)))
        {
            throw Invalid($"{array.Name.LocalName} must contain only {itemName} entries.");
        }
        return array.Elements();
    }

    private static string ResolveSchemaType(XElement value)
    {
        var qualified = value.Attribute(Xsi + "type")?.Value ?? throw Invalid("A RecordSet value requires xsi:type.");
        var parts = qualified.Split(':');
        if (parts.Length != 2 || value.GetNamespaceOfPrefix(parts[0]) != Xsd)
        {
            throw Invalid("RecordSet attribute values require an XML Schema type.");
        }
        return parts[1];
    }

    private static XElement RequiredElement(XElement parent, string name)
        => parent.Element(name) ?? throw Invalid($"{parent.Name.LocalName} requires {name}.");

    private static bool XmlBoolean(XElement value)
    {
        if (value.HasElements)
        {
            throw Invalid($"{value.Name.LocalName} requires a boolean value.");
        }
        return value.Value.Trim() switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => throw Invalid($"{value.Name.LocalName} requires a boolean value.")
        };
    }

    private static int? OptionalInt(XElement parent, string name)
        => parent.Element(name) is { } element
            ? int.TryParse(element.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && !element.HasElements
                ? value
                : throw Invalid($"{name} requires an integer value.")
            : null;

    private static double Finite(double value)
        => double.IsFinite(value) ? value : throw Invalid("RecordSet numeric values must be finite.");

    private sealed record SoapField(string Name, string Type, string? Alias, string? GeometryType, bool HasZ, bool HasM,
        int? Wkid, int? LatestWkid);

    private sealed record GeometryEncoding(string GeometryType, bool HasZ, bool HasM);
}
