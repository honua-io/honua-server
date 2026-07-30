// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml;
using System.Xml.Schema;

namespace Honua.Protocols.Ogc.Classic.Wps20;

/// <summary>
/// Provides the trusted schema used to constrain WPS XML request envelopes.
/// Nested operation content remains subject to the adapter's bounded semantic validation.
/// </summary>
/// <remarks>
/// The schema is compiled in-process from the constant below: no network access, no
/// <c>schemaLocation</c> resolution, and no inline-schema processing, so a request cannot
/// steer the validator at attacker-chosen schema content.
/// <para>
/// Deliberate protocol divergence: because only the five dispatched request roots are
/// declared, an XML POST whose root is an undeclared element in the WPS namespace is
/// rejected as <c>InvalidParameterValue</c> (400) instead of reaching the operation switch
/// and returning <c>OperationNotSupported</c> (501). The KVP/GET path is unchanged and still
/// answers 501 for an unknown <c>request=</c> value. Rationale and the pinning test are
/// recorded in docs/internal/security/code-scanning-2026-Q2-remediation.md.
/// </para>
/// </remarks>
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
        using var input = new StringReader(SchemaDocument);
        using var schemaReader = XmlReader.Create(input, settings);
        schemas.Add(Wps20Endpoint.WpsNamespace, schemaReader);
        schemas.Compile();
        return schemas;
    }
}
