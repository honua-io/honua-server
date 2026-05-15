// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Models;
using Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Services;
using System.Reflection;
using System.Xml;
using System.Xml.Serialization;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>
/// XML validation tests for the WFS 2.0 GetCapabilities response.
/// </summary>
public class Wfs20CapabilitiesXmlValidationTests
{
    [Fact]
    public void GetCapabilitiesXml_TemporalConformance_ShouldAdvertiseMinimumTemporalFilterSupport()
    {
        var xml = SerializeToXml(CreateWfsCapabilitiesWithFilterCapabilities());

        xml.Should().Contain("Temporal_Capabilities");
        xml.Should().MatchRegex(@"<([A-Za-z_][\w.-]*:)?TemporalOperators(\s|>|/)");
        xml.Should().Contain("name=\"After\"");
        xml.Should().Contain("name=\"Before\"");
        xml.Should().Contain("name=\"During\"");
        xml.Should().NotContain("name=\"AnyInteracts\"");
        xml.Should().MatchRegex(@"name=""ImplementsMinTemporalFilter""[^>]*>[\s\S]*?TRUE[\s\S]*?</");
        xml.Should().MatchRegex(@"name=""ImplementsTemporalFilter""[^>]*>[\s\S]*?FALSE[\s\S]*?</");
        xml.Should().MatchRegex(@"name=""ImplementsTemporalInstant""[^>]*>[\s\S]*?TRUE[\s\S]*?</");
        xml.Should().MatchRegex(@"name=""ImplementsTemporalPeriod""[^>]*>[\s\S]*?TRUE[\s\S]*?</");
    }

    [Fact]
    public void GetCapabilitiesXml_FunctionsAndCql2_ShouldNotAdvertiseUnsupportedCapabilities()
    {
        var xml = SerializeToXml(CreateWfsCapabilitiesWithFilterCapabilities());

        xml.Should().NotContain("<Functions");
        xml.Should().NotContain("name=\"ST_Area\"");
        xml.Should().NotContain("name=\"UPPER\"");
        xml.Should().MatchRegex(@"name=""ImplementsFunctions""[^>]*>[\s\S]*?FALSE[\s\S]*?</");
        xml.Should().MatchRegex(@"name=""ImplementsArithmeticOperators""[^>]*>[\s\S]*?FALSE[\s\S]*?</");
        xml.Should().MatchRegex(@"name=""ImplementsExtendedOperators""[^>]*>[\s\S]*?TRUE[\s\S]*?</");
        xml.Should().MatchRegex(@"name=""ImplementsCQL2Text""[^>]*>[\s\S]*?FALSE[\s\S]*?</");
        xml.Should().MatchRegex(@"name=""ImplementsCQL2JSON""[^>]*>[\s\S]*?FALSE[\s\S]*?</");
        xml.Should().MatchRegex(@"name=""ImplementsCQL2ArrayOperators""[^>]*>[\s\S]*?FALSE[\s\S]*?</");
        xml.Should().MatchRegex(@"name=""ImplementsCQL2Functions""[^>]*>[\s\S]*?FALSE[\s\S]*?</");
    }

    [Fact]
    public void GetCapabilitiesXml_Structure_ShouldFollowOGCSchemaRequirements()
    {
        var xml = SerializeToXml(CreateWfsCapabilitiesWithFilterCapabilities());

        xml.Should().Contain("Filter_Capabilities");
        xml.Should().Contain("Conformance");
        xml.Should().Contain("Scalar_Capabilities");
        xml.Should().Contain("Spatial_Capabilities");
        xml.Should().Contain("Temporal_Capabilities");
        xml.Should().Contain("xmlns:fes=");
        xml.Should().Contain("xmlns:gml=");
        xml.Should().Contain("xmlns:xsd=");

        var xmlDoc = new XmlDocument();
        var parseAction = () => xmlDoc.LoadXml(xml);
        parseAction.Should().NotThrow("Generated XML should be well-formed and parseable");

        xml.Should().MatchRegex(@"<[\s\S]*?ComparisonOperators[^>]*>[\s\S]*?PropertyIsEqualTo[\s\S]*?</");
        xml.Should().MatchRegex(@"<[\s\S]*?GeometryOperands[^>]*>[\s\S]*?Point[\s\S]*?</");
        xml.Should().NotContain("name=\"PropertyIsIn\"");
        xml.Should().NotContain("name=\"Relate\"");
    }

    private static WfsCapabilities CreateWfsCapabilitiesWithFilterCapabilities()
    {
        var type = typeof(Wfs20Handler);
        var method = type.GetMethod("BuildFilterCapabilities",
            BindingFlags.NonPublic | BindingFlags.Static);
        var filterCapabilities = (FilterCapabilities)method!.Invoke(null, null)!;

        return new WfsCapabilities
        {
            Version = "2.0.0",
            ServiceIdentification = new ServiceIdentification
            {
                Title = "Honua WFS",
                ServiceType = "WFS",
                ServiceTypeVersion = ["2.0.0"]
            },
            ServiceProvider = new ServiceProvider
            {
                ProviderName = "Honua"
            },
            FilterCapabilities = filterCapabilities
        };
    }

    private static string SerializeToXml(WfsCapabilities capabilities)
    {
        var serializer = new XmlSerializer(typeof(WfsCapabilities));
        using var stringWriter = new StringWriter();
        using var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = false,
            Encoding = System.Text.Encoding.UTF8
        });

        serializer.Serialize(xmlWriter, capabilities, capabilities.Namespaces);
        return stringWriter.ToString();
    }
}
