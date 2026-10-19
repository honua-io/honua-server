// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml;
using FluentAssertions;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>
/// Verifies that GmlSerializer.SerializeGeometry prohibits DTD declarations
/// explicitly (PA-107), consistent with SecureXmlDocumentParser used elsewhere.
/// </summary>
public sealed class GmlSerializerDtdTests
{
    [UnitTest]
    public void SerializeGeometry_DtdDeclaration_ThrowsXmlException()
    {
        // PA-107: a GML fragment with an embedded DTD declaration must not be processed —
        // the explicit DtdProcessing.Prohibit setting should throw before parsing the body.
        var gmlWithDtd = """
            <!DOCTYPE geometry [<!ENTITY xxe "test">]>
            <gml:Point xmlns:gml="http://www.opengis.net/gml/3.2">
              <gml:pos>1 2</gml:pos>
            </gml:Point>
            """;

        var serializer = new GmlSerializer();

        // SerializeGeometry must not return a non-null result for a DTD-bearing payload,
        // and specifically must not resolve the external entity.
        // The XmlException from DtdProcessing.Prohibit is caught internally and returns null.
        var result = serializer.SerializeGeometry(gmlWithDtd);

        // The method swallows XmlException and returns null — verifying it does not
        // propagate a parsed result (which would indicate the DTD was processed).
        result.Should().BeNull("DTD-bearing GML must not produce a parsed output element");
    }

    [UnitTest]
    public void SerializeGeometry_ValidGml_ReturnsElement()
    {
        // Regression guard: well-formed GML without DTD still serialises correctly after
        // the explicit XmlReaderSettings are applied.
        var validGml = """
            <gml:Point xmlns:gml="http://www.opengis.net/gml/3.2">
              <gml:pos>1 2</gml:pos>
            </gml:Point>
            """;

        var serializer = new GmlSerializer();
        var result = serializer.SerializeGeometry(validGml);

        result.Should().NotBeNull("valid GML must still serialise correctly");
        result!.Name.LocalName.Should().Be("geometryProperty");
    }

    [UnitTest]
    public void SerializeGeometry_NullInput_ReturnsNull()
    {
        var serializer = new GmlSerializer();
        var result = serializer.SerializeGeometry(null);
        result.Should().BeNull();
    }
}
