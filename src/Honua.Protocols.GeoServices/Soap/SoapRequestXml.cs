// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml;
using System.Xml.Schema;

namespace Honua.Protocols.GeoServices.Soap;

/// <summary>
/// Reads the supported SOAP envelope contract without trusting request-supplied
/// schemas, DTDs, or external resources. Adapters validate operation payloads.
/// </summary>
internal static class SoapRequestXml
{
    private static readonly XmlSchemaSet _envelopeSchemas = CreateEnvelopeSchemas();

    internal static string GetSafeErrorMessage(Exception exception) =>
        exception is XmlSchemaValidationException
            ? "Malformed SOAP request. The envelope must contain exactly one Body element with exactly one operation, after any Header."
            : "Malformed SOAP request.";

    public static XmlReader CreateReader(Stream body, long maxCharacters)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1_024,
            MaxCharactersInDocument = maxCharacters,
            ValidationType = ValidationType.Schema,
            // Never process inline schemas or xsi:schemaLocation from the client.
            ValidationFlags = XmlSchemaValidationFlags.None,
            Schemas = _envelopeSchemas
        };
        return XmlReader.Create(body, settings);
    }

    private static XmlSchemaSet CreateEnvelopeSchemas()
    {
        var schemas = new XmlSchemaSet { XmlResolver = null };
        foreach (var soapNamespace in new[]
        {
            "http://schemas.xmlsoap.org/soap/envelope/",
            "http://www.w3.org/2003/05/soap-envelope"
        })
        {
            // Both adapters support one optional Header followed by one Body.
            // ArcGIS operation namespaces vary by client version, so their
            // names and typed/bounded arguments remain the adapter's contract.
            // Skip those subtrees explicitly, including client xsi:type payloads.
            var schema = $$"""
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                           targetNamespace="{{soapNamespace}}" elementFormDefault="qualified">
                  <xs:element name="Envelope">
                    <xs:complexType>
                      <xs:sequence>
                        <xs:element name="Header" minOccurs="0">
                          <xs:complexType>
                            <xs:sequence><xs:any namespace="##other" processContents="skip" minOccurs="0" maxOccurs="unbounded" /></xs:sequence>
                            <xs:anyAttribute processContents="lax" />
                          </xs:complexType>
                        </xs:element>
                        <xs:element name="Body">
                          <xs:complexType>
                            <xs:sequence><xs:any namespace="##other" processContents="skip" /></xs:sequence>
                            <xs:anyAttribute processContents="lax" />
                          </xs:complexType>
                        </xs:element>
                      </xs:sequence>
                      <xs:anyAttribute processContents="lax" />
                    </xs:complexType>
                  </xs:element>
                </xs:schema>
                """;
            using var text = new StringReader(schema);
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 1_024,
                MaxCharactersInDocument = 16_384
            });
            schemas.Add(soapNamespace, reader);
        }

        schemas.Compile();
        return schemas;
    }
}
