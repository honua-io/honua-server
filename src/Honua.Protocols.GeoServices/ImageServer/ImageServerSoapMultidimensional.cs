// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Xml.Linq;
using Honua.Protocols.GeoServices.ImageServer.Models;
using static Honua.Protocols.GeoServices.Soap.ArcGisSoapProtocol;

namespace Honua.Protocols.GeoServices.ImageServer;

internal static partial class ImageServerSoapEndpoints
{
    // MultidimensionalInfo is a parallel Names/Values container of PropertySets,
    // not the REST variables/dimensions document. StdTime SOAP coordinates are
    // OLE Automation days; the domain and REST retain Unix milliseconds.
    internal static XElement BuildMultidimensionalResult(ImageServerMultidimensionalInfo? info)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        var variables = info?.Variables ?? [];
        return new XElement("Result", new XAttribute(xsi + "type", "tns:MultidimensionalInfo"),
            new XElement("Names", new XAttribute(xsi + "type", "tns:ArrayOfString"),
                new XElement("String", "Variables"),
                new XElement("String", "DimensionAttributes"),
                new XElement("String", "DimensionValues")),
            new XElement("Values", new XAttribute(xsi + "type", "tns:ArrayOfAnyType"),
                MultidimensionalPropertySet("AnyType", variables.Select(variable =>
                    MultidimensionalProperty(variable.Name, MultidimensionalPropertySet("Value",
                        VariableProperties(variable, xsi))))),
                MultidimensionalPropertySet("AnyType", variables.Select(variable =>
                    MultidimensionalProperty(variable.Name, MultidimensionalPropertySet("Value",
                        variable.Dimensions.Select(dimension => MultidimensionalProperty(dimension.Name,
                            MultidimensionalPropertySet("Value", DimensionProperties(dimension, xsi)))))))),
                MultidimensionalPropertySet("AnyType", variables.Select(variable =>
                    MultidimensionalProperty(variable.Name, MultidimensionalPropertySet("Value",
                        variable.Dimensions.Where(static dimension => dimension.Values is not null)
                            .Select(dimension => MultidimensionalProperty(dimension.Name,
                                DimensionCoordinates(dimension)))))))));
    }

    private static List<XElement> VariableProperties(ImageServerMultidimensionalVariable variable, XNamespace xsi)
    {
        var properties = new List<XElement>();
        if (variable.Unit is not null) { properties.Add(BuildProperty("Unit", variable.Unit, "xsd:string", xsi)); }
        if (variable.Description is not null) { properties.Add(BuildProperty("Description", variable.Description, "xsd:string", xsi)); }
        return properties;
    }

    private static List<XElement> DimensionProperties(ImageServerMultidimensionalDimension dimension, XNamespace xsi)
    {
        var properties = new List<XElement>
        {
            BuildProperty("Count", dimension.DimensionSize.ToString(CultureInfo.InvariantCulture),
                dimension.DimensionSize is >= int.MinValue and <= int.MaxValue ? "xsd:int" : "xsd:long", xsi),
            BuildProperty("HasRegularIntervals", dimension.HasRegularIntervals ? "true" : "false", "xsd:boolean", xsi),
            // The existing domain represents point coordinates, not intervals/ranges.
            BuildProperty("HasRanges", "false", "xsd:boolean", xsi)
        };
        if (dimension.Unit is not null) { properties.Add(BuildProperty("Unit", dimension.Unit, "xsd:string", xsi)); }
        if (dimension.Extent is { Length: >= 2 } extent)
        {
            properties.Add(BuildProperty("Minimum", FormatDouble(SoapCoordinate(dimension, extent[0])), "xsd:double", xsi));
            properties.Add(BuildProperty("Maximum", FormatDouble(SoapCoordinate(dimension, extent[1])), "xsd:double", xsi));
        }
        if (dimension.HasRegularIntervals && dimension.Values is { Length: >= 2 } values)
        {
            // Calculate time intervals in the source units before converting dates:
            // negative OLE Automation dates do not have linear fractional semantics.
            var temporal = IsSoapTimeDimension(dimension);
            var interval = (values[1] - values[0]) / (temporal ? 86_400_000 : 1);
            properties.Add(BuildProperty("Interval", FormatDouble(interval), "xsd:double", xsi));
            var intervalUnit = temporal ? "Days" : dimension.Unit;
            if (intervalUnit is not null) { properties.Add(BuildProperty("IntervalUnit", intervalUnit, "xsd:string", xsi)); }
        }
        return properties;
    }

    private static XElement DimensionCoordinates(ImageServerMultidimensionalDimension dimension)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        var coordinates = dimension.Values!.Select(value => SoapCoordinate(dimension, value)).ToArray();
        // Point coordinates have equal lower/upper bounds, in the same order.
        return new XElement("Value", new XAttribute(xsi + "type", "tns:ArrayOfArgument"),
            MultidimensionalCoordinateArgument(coordinates), MultidimensionalCoordinateArgument(coordinates));
    }

    private static XElement MultidimensionalCoordinateArgument(double[] coordinates)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        return new XElement("Argument", new XAttribute(xsi + "type", "tns:ArrayOfDouble"),
            coordinates.Select(static value => new XElement("Double", FormatDouble(value))));
    }

    private static bool IsSoapTimeDimension(ImageServerMultidimensionalDimension dimension)
        => string.Equals(dimension.Name, "StdTime", StringComparison.OrdinalIgnoreCase);

    private static double SoapCoordinate(ImageServerMultidimensionalDimension dimension, double value)
        => IsSoapTimeDimension(dimension) ? DateTime.UnixEpoch.AddMilliseconds(value).ToOADate() : value;

    private static XElement MultidimensionalPropertySet(string name, IEnumerable<XElement> properties)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        return new XElement(name, new XAttribute(xsi + "type", "tns:PropertySet"),
            new XElement("PropertyArray", new XAttribute(xsi + "type", "tns:ArrayOfPropertySetProperty"), properties));
    }

    private static XElement MultidimensionalProperty(string key, XElement value)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        return new XElement("PropertySetProperty", new XAttribute(xsi + "type", "tns:PropertySetProperty"),
            new XElement("Key", key), value);
    }
}
