// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml;
using System.Xml.Schema;

namespace Honua.Protocols.Ogc.Classic.Wps20;

/// <summary>
/// Provides the trusted schema used to constrain WPS XML request envelopes.
/// Nested operation content remains subject to the adapter's bounded semantic validation.
/// </summary>
internal static class Wps20RequestSchema
{
    internal static XmlSchemaSet SchemaSet { get; } = CreateSchemaSet();

    private const string SchemaDocument = """
        <?xml version="1.0" encoding="utf-8"?>
        <xs:schema
            xmlns:xs="http://www.w3.org/2001/XMLSchema"
            xmlns:wps="http://www.opengis.net/wps/2.0"
            targetNamespace="http://www.opengis.net/wps/2.0"
            elementFormDefault="qualified"
            attributeFormDefault="unqualified">
          <xs:complexType name="RequestEnvelopeType" mixed="true">
            <xs:sequence>
              <xs:any minOccurs="0" maxOccurs="unbounded" processContents="skip"/>
            </xs:sequence>
            <xs:anyAttribute processContents="skip"/>
          </xs:complexType>
          <xs:element name="GetCapabilities" type="wps:RequestEnvelopeType"/>
          <xs:element name="DescribeProcess" type="wps:RequestEnvelopeType"/>
          <xs:element name="Execute" type="wps:RequestEnvelopeType"/>
          <xs:element name="GetStatus" type="wps:RequestEnvelopeType"/>
          <xs:element name="GetResult" type="wps:RequestEnvelopeType"/>
        </xs:schema>
        """;

    private static XmlSchemaSet CreateSchemaSet()
    {
        var schemas = new XmlSchemaSet
        {
            XmlResolver = null
        };
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
        using var schemaReader = XmlReader.Create(new StringReader(SchemaDocument), settings);
        schemas.Add(Wps20Endpoint.WpsNamespace, schemaReader);
        schemas.Compile();
        return schemas;
    }
}
