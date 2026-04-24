// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.Core.Features.Catalog.Domain;

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Generates XML Schema (XSD) definitions for WFS 2.0 DescribeFeatureType operations.
/// </summary>
internal sealed class Wfs20FeatureTypeSchemaGenerator : IWfs20FeatureTypeSchemaGenerator
{
    private static readonly XNamespace XsdNamespace = "http://www.w3.org/2001/XMLSchema";
    private static readonly XNamespace GmlNamespace = "http://www.opengis.net/gml/3.2";

    /// <inheritdoc />
    public XDocument GenerateFeatureTypeSchema(LayerDefinition layer, string targetNamespace, string featureTypeName)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(featureTypeName);

        var targetNs = XNamespace.Get(targetNamespace);

        var schema = new XElement(XsdNamespace + "schema",
            new XAttribute("targetNamespace", targetNamespace),
            new XAttribute("elementFormDefault", "qualified"),
            new XAttribute(XNamespace.Xmlns + "xs", XsdNamespace.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "gml", GmlNamespace.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "honua", targetNamespace)
        );

        // Import GML schema
        schema.Add(new XElement(XsdNamespace + "import",
            new XAttribute("namespace", GmlNamespace.NamespaceName),
            new XAttribute("schemaLocation", "http://schemas.opengis.net/gml/3.2.1/gml.xsd")
        ));

        // Define the feature type
        var featureTypeElement = new XElement(XsdNamespace + "element",
            new XAttribute("name", featureTypeName),
            new XAttribute("type", $"honua:{featureTypeName}Type"),
            new XAttribute("substitutionGroup", "gml:AbstractFeature")
        );
        schema.Add(featureTypeElement);

        // Define the feature type complex type
        var complexType = new XElement(XsdNamespace + "complexType",
            new XAttribute("name", $"{featureTypeName}Type")
        );

        var complexContent = new XElement(XsdNamespace + "complexContent");
        var extension = new XElement(XsdNamespace + "extension",
            new XAttribute("base", "gml:AbstractFeatureType")
        );

        var sequence = new XElement(XsdNamespace + "sequence");

        // Add geometry property if layer has geometry
        if (layer.GeometryType != GeometryType.None)
        {
            var geometryElement = new XElement(XsdNamespace + "element",
                new XAttribute("name", "geometry"),
                new XAttribute("type", "gml:GeometryPropertyType"),
                new XAttribute("minOccurs", "0"),
                new XAttribute("maxOccurs", "1")
            );
            sequence.Add(geometryElement);
        }

        // Add field elements
        foreach (var fieldElement in GenerateFieldElements(layer, targetNamespace))
        {
            sequence.Add(fieldElement);
        }

        extension.Add(sequence);
        complexContent.Add(extension);
        complexType.Add(complexContent);
        schema.Add(complexType);

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            schema
        );
    }

    /// <inheritdoc />
    public IEnumerable<XElement> GenerateFieldElements(LayerDefinition layer, string targetNamespace)
    {
        ArgumentNullException.ThrowIfNull(layer);

        foreach (var field in layer.Fields)
        {
            // Skip system fields like objectid and shape
            if (IsSystemField(field.Name))
            {
                continue;
            }

            var element = new XElement(XsdNamespace + "element",
                new XAttribute("name", field.Name),
                new XAttribute("type", MapFieldTypeToXsdType(field.Type)),
                new XAttribute("minOccurs", field.Nullable ? "0" : "1"),
                new XAttribute("maxOccurs", "1")
            );

            // Add length constraint for string fields
            if (field.Type == FieldType.String && field.Length.HasValue && field.Length.Value > 0)
            {
                var simpleType = new XElement(XsdNamespace + "simpleType");
                var restriction = new XElement(XsdNamespace + "restriction",
                    new XAttribute("base", "xs:string")
                );
                restriction.Add(new XElement(XsdNamespace + "maxLength",
                    new XAttribute("value", field.Length.Value.ToString())
                ));
                simpleType.Add(restriction);
                element.Add(simpleType);
                element.SetAttributeValue("type", null); // Remove type attribute when using inline simpleType
            }

            yield return element;
        }
    }

    /// <inheritdoc />
    public string MapFieldTypeToXsdType(FieldType fieldType)
    {
        return fieldType switch
        {
            FieldType.String => "xs:string",
            FieldType.Integer => "xs:int",
            FieldType.BigInteger => "xs:long",
            FieldType.Double => "xs:double",
            FieldType.Float => "xs:float",
            FieldType.Boolean => "xs:boolean",
            FieldType.DateTime => "xs:dateTime",
            FieldType.Date => "xs:date",
            FieldType.Time => "xs:time",
            FieldType.Uuid => "xs:string", // GUIDs as strings in XSD
            FieldType.Binary => "xs:base64Binary",
            FieldType.Geometry => "gml:GeometryPropertyType",
            FieldType.Json => "xs:string", // JSON as string
            _ => "xs:string" // Default fallback
        };
    }

    private static bool IsSystemField(string fieldName)
    {
        return fieldName.Equals("objectid", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Equals("shape", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Equals("geometry", StringComparison.OrdinalIgnoreCase);
    }
}
