// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using static Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer.ImageServerMultidimensionalSoapAssertions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

internal static class ImageServerMultidimensionalSoapAssertions
{
    private const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly string[] SetNames = ["Variables", "DimensionAttributes", "DimensionValues"];

    internal static XElement[] AssertShape(XElement result)
    {
        Type(result).Should().Be("tns:MultidimensionalInfo");
        result.Elements().Select(element => element.Name.LocalName).Should().Equal("Names", "Values");
        var names = Child(result, "Names");
        Type(names).Should().Be("tns:ArrayOfString");
        names.Elements().Select(element => element.Value).Should().Equal(SetNames);
        var values = Child(result, "Values");
        Type(values).Should().Be("tns:ArrayOfAnyType");
        var sets = values.Elements().ToArray();
        sets.Should().HaveCount(3);
        foreach (var set in sets)
        {
            set.Name.LocalName.Should().Be("AnyType");
            Type(set).Should().Be("tns:PropertySet");
            Type(Child(set, "PropertyArray")).Should().Be("tns:ArrayOfPropertySetProperty");
        }
        foreach (var property in result.Descendants().Where(element => element.Name.LocalName == "PropertySetProperty"))
        {
            Type(property).Should().Be("tns:PropertySetProperty");
            property.Elements().Select(element => element.Name.LocalName).Should().Equal("Key", "Value");
        }
        foreach (var value in result.Descendants().Where(element => Type(element) == "tns:ArrayOfArgument"))
        {
            value.Elements().Should().HaveCount(2);
            foreach (var argument in value.Elements())
            {
                argument.Name.LocalName.Should().Be("Argument");
                Type(argument).Should().Be("tns:ArrayOfDouble");
                argument.Elements().Select(element => element.Name.LocalName).Should().OnlyContain(name => name == "Double");
            }
        }
        return sets;
    }

    internal static XElement Child(XElement element, string name)
        => element.Elements().Single(child => child.Name.LocalName == name);
    internal static XElement Property(XElement set, string key)
        => Child(Child(set, "PropertyArray").Elements().Single(property => Child(property, "Key").Value == key), "Value");
    internal static string? Type(XElement element) => element.Attribute(XName.Get("type", Xsi))?.Value;
    internal static double Number(XElement element) => double.Parse(element.Value, CultureInfo.InvariantCulture);
}
