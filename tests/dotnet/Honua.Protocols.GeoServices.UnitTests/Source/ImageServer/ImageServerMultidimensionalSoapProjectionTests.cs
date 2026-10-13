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

// Pure serialization tests deliberately have no database collection or fixture.
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerMultidimensionalSoapProjectionTests
{
    private static readonly double[] FixtureDepths = [-10, -5, 0];

    [UnitTheory]
    [InlineData("2024-01-01T00:00:00Z", 45292)]
    [InlineData("1899-12-29T06:00:00Z", -1.25)]
    [InlineData("1899-12-30T06:00:00Z", 0.25)]
    [Operation(Operations.GetServiceInfo)]
    public void TemporalCoordinates_UseOleAutomationDateAndRoundTrip(string isoDate, double expectedDays)
    {
        var date = DateTimeOffset.Parse(isoDate, CultureInfo.InvariantCulture);
        var milliseconds = (double)date.ToUnixTimeMilliseconds();
        var info = new ImageServerMultidimensionalInfo
        {
            Variables = [new ImageServerMultidimensionalVariable
            {
                Name = "temperature", Dimensions = [new ImageServerMultidimensionalDimension
                {
                    Name = "StdTime", DimensionSize = 1, Extent = [milliseconds, milliseconds], Values = [milliseconds]
                }]
            }]
        };
        var sets = AssertShape(ImageServerSoapEndpoints.BuildMultidimensionalResult(info));
        var attributes = Property(Property(sets[1], "temperature"), "StdTime");
        Number(Property(attributes, "Minimum")).Should().Be(expectedDays);
        Number(Property(attributes, "Maximum")).Should().Be(expectedDays);
        foreach (var bounds in Property(Property(sets[2], "temperature"), "StdTime").Elements())
        {
            var coordinate = bounds.Elements().Should().ContainSingle().Subject;
            Number(coordinate).Should().Be(expectedDays);
            DateTime.FromOADate(Number(coordinate)).Ticks.Should().Be(date.UtcDateTime.Ticks);
        }
        info.Variables[0].Dimensions[0].Values![0].Should().Be(milliseconds, "SOAP must not mutate REST/domain coordinates");
    }

    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Operation(Operations.GetServiceInfo)]
    public void EmptyMetadata_PreservesThreeEmptyTypedPropertySets(bool absent)
    {
        var sets = AssertShape(ImageServerSoapEndpoints.BuildMultidimensionalResult(
            absent ? null : new ImageServerMultidimensionalInfo()));
        foreach (var set in sets) { Child(set, "PropertyArray").Elements().Should().BeEmpty(); }
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public void NumericAndUnknownDimensions_PreserveValuesAndDoNotInventCoordinates()
    {
        var info = new ImageServerMultidimensionalInfo
        {
            Variables =
            [
                new ImageServerMultidimensionalVariable
                {
                    Name = "temperature", Unit = "degC", Description = "Water temperature",
                    Dimensions =
                    [
                        new ImageServerMultidimensionalDimension
                        {
                            Name = "StdZ", Unit = "Meters", DimensionSize = 3,
                            Extent = [-10, 0], Values = FixtureDepths, HasRegularIntervals = true
                        },
                        new ImageServerMultidimensionalDimension { Name = "x", DimensionSize = 64 }
                    ]
                },
                new ImageServerMultidimensionalVariable { Name = "salinity", Dimensions = [] }
            ]
        };
        var sets = AssertShape(ImageServerSoapEndpoints.BuildMultidimensionalResult(info));
        Property(Property(sets[0], "temperature"), "Unit").Value.Should().Be("degC");
        Child(Property(sets[0], "salinity"), "PropertyArray").Elements().Should().BeEmpty();
        var attributes = Property(sets[1], "temperature");
        var depth = Property(attributes, "StdZ");
        Type(Property(depth, "Count")).Should().Be("xsd:int");
        Number(Property(depth, "Count")).Should().Be(3);
        Number(Property(depth, "Minimum")).Should().Be(-10);
        Number(Property(depth, "Maximum")).Should().Be(0);
        Number(Property(depth, "Interval")).Should().Be(5);
        Property(depth, "IntervalUnit").Value.Should().Be("Meters");
        Property(depth, "HasRanges").Value.Should().Be("false");
        var coordinates = Property(sets[2], "temperature");
        foreach (var bounds in Property(coordinates, "StdZ").Elements())
        {
            bounds.Elements().Select(Number).Should().Equal(FixtureDepths);
        }
        var unknown = Property(attributes, "x");
        Child(unknown, "PropertyArray").Elements().Select(property => Child(property, "Key").Value)
            .Should().BeEquivalentTo("Count", "HasRegularIntervals", "HasRanges");
        Child(coordinates, "PropertyArray").Elements().Select(property => Child(property, "Key").Value)
            .Should().Equal("StdZ");
        Child(Property(sets[2], "salinity"), "PropertyArray").Elements().Should().BeEmpty();
    }

}
