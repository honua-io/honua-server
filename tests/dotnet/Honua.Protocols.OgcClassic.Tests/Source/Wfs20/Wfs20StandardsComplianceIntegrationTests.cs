// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.Ogc.Classic.Wfs20.Models;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using System.Xml;
using System.Xml.Serialization;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>
/// Integration tests for WFS 2.0 standards-facing capability metadata.
/// </summary>
public class Wfs20StandardsComplianceIntegrationTests
{
    [Fact]
    public void WfsCapabilities_WithFilterCapabilities_ShouldSerializeToValidXml()
    {
        var capabilities = CreateCompleteWfsCapabilities();
        var xml = SerializeCapabilities(capabilities);

        xml.Should().NotBeEmpty();
        xml.Should().Contain("Filter_Capabilities");
        xml.Should().Contain("Conformance");
        xml.Should().MatchRegex(@"<([A-Za-z_][\w.-]*:)?TemporalOperators(\s|>|/)");
        xml.Should().Contain("Temporal_Capabilities");
        xml.Should().Contain("SpatialOperators");
        xml.Should().Contain("ComparisonOperators");
        xml.Should().NotContain("<Functions");
        xml.Should().NotContain("ST_Area");

        var xmlDoc = new XmlDocument();
        var action = () => xmlDoc.LoadXml(xml);
        action.Should().NotThrow("Serialized XML should be well-formed");
    }

    [Fact]
    public void FilterCapabilities_TemporalCapabilities_ShouldAdvertiseMinimumTemporalOperators()
    {
        var capabilities = CreateCompleteWfsCapabilities();

        capabilities.FilterCapabilities!.TemporalCapabilities.Should().NotBeNull();
        capabilities.FilterCapabilities.TemporalCapabilities!.TemporalOperators!.Operators
            .Select(op => op.Name)
            .Should().BeEquivalentTo("After", "Before", "During");
    }

    [Fact]
    public void FilterCapabilities_ConformanceConstraints_ShouldMatchRuntimeSupport()
    {
        var capabilities = CreateCompleteWfsCapabilities();
        var constraints = capabilities.FilterCapabilities!.Conformance.Constraints;
        var constraintValues = constraints.ToDictionary(c => c.Name, c => c.DefaultValue);

        foreach (var required in new[]
                 {
                     "ImplementsQuery",
                     "ImplementsAdHocQuery",
                     "ImplementsResourceId",
                     "ImplementsStandardFilter",
                     "ImplementsMinSpatialFilter",
                     "ImplementsSpatialFilter",
                     "ImplementsMinTemporalFilter",
                     "ImplementsTemporalInstant",
                     "ImplementsTemporalPeriod"
                 })
        {
            constraintValues[required].Should().Be("TRUE");
        }

        foreach (var supported in new[]
                 {
                     "ImplementsExtendedOperators"
                 })
        {
            constraintValues[supported].Should().Be("TRUE");
        }

        foreach (var unsupported in new[]
                 {
                     "ImplementsFunctions",
                     "ImplementsArithmeticOperators",
                     "ImplementsCQL2Text",
                     "ImplementsCQL2JSON",
                     "ImplementsCQL2SpatialOperators",
                     "ImplementsCQL2TemporalOperators",
                     "ImplementsCQL2Functions",
                     "ImplementsTemporalFilter"
                 })
        {
            constraintValues[unsupported].Should().Be("FALSE");
        }

        constraintValues["ImplementsCQL2ArrayOperators"].Should().Be("FALSE");
    }

    [Fact]
    public void FilterCapabilities_SpatialCapabilities_ShouldAdvertiseSupportedGeometryModel()
    {
        var capabilities = CreateCompleteWfsCapabilities();
        var spatialCaps = capabilities.FilterCapabilities!.SpatialCapabilities!;

        spatialCaps.GeometryOperands!.Operands
            .Select(op => op.Name.Name)
            .Should().BeEquivalentTo("Envelope", "Point", "LineString", "Curve", "Polygon", "Surface");

        var spatialOperators = spatialCaps.SpatialOperators!.Operators
            .Select(op => op.Name)
            .ToArray();

        foreach (var expected in new[]
                 {
                     "BBOX",
                     "Intersects",
                     "Contains",
                     "Within",
                     "Crosses",
                     "Touches",
                     "Overlaps",
                     "Disjoint",
                     "Equals",
                     "DWithin",
                     "Beyond"
                 })
        {
            spatialOperators.Should().Contain(expected);
        }
    }

    private static WfsCapabilities CreateCompleteWfsCapabilities()
    {
        var type = typeof(Wfs20Handler);
        var method = type.GetMethod("BuildFilterCapabilities",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var filterCapabilities = (FilterCapabilities)method!.Invoke(null, null)!;

        return new WfsCapabilities
        {
            ServiceIdentification = new ServiceIdentification(),
            ServiceProvider = new ServiceProvider(),
            FilterCapabilities = filterCapabilities
        };
    }

    private static string SerializeCapabilities(WfsCapabilities capabilities)
    {
        var serializer = new XmlSerializer(typeof(WfsCapabilities));
        using var stringWriter = new StringWriter();
        using var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = false,
            Encoding = System.Text.Encoding.UTF8
        });

        serializer.Serialize(xmlWriter, capabilities);
        return stringWriter.ToString();
    }
}
