// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.WebUtilities;

namespace Honua.Server.Features.Identity.Saml;

internal sealed record SamlAuthnRequest(string Id, string Xml);

// Builds the SP-initiated AuthnRequest and encodes it for the HTTP-Redirect binding. The request is
// unsigned (metadata advertises AuthnRequestsSigned=false); its ID is what the ACS later correlates.
internal static class SamlAuthnRequestBuilder
{
    private const string SamlAssertionNs = "urn:oasis:names:tc:SAML:2.0:assertion";
    private const string SamlProtocolNs = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string HttpPostBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST";

    public static SamlAuthnRequest Create(SamlAuthenticationOptions options, DateTimeOffset issueInstant)
    {
        ArgumentNullException.ThrowIfNull(options);
        var id = "_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = true,
        };

        var output = new StringBuilder();
        using (var writer = XmlWriter.Create(output, settings))
        {
            writer.WriteStartElement("samlp", "AuthnRequest", SamlProtocolNs);
            writer.WriteAttributeString("xmlns", "saml", null, SamlAssertionNs);
            writer.WriteAttributeString("ID", id);
            writer.WriteAttributeString("Version", "2.0");
            writer.WriteAttributeString("IssueInstant", issueInstant.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("Destination", options.IdpSingleSignOnServiceUrl?.Trim());
            writer.WriteAttributeString("AssertionConsumerServiceURL", options.AssertionConsumerServiceUrl);
            writer.WriteAttributeString("ProtocolBinding", HttpPostBinding);
            writer.WriteElementString("saml", "Issuer", SamlAssertionNs, options.EntityId);
            writer.WriteEndElement();
        }

        return new SamlAuthnRequest(id, output.ToString());
    }

    // HTTP-Redirect binding: raw DEFLATE (no zlib header), then base64.
    public static string EncodeForRedirect(string requestXml)
    {
        var bytes = Encoding.UTF8.GetBytes(requestXml);
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(bytes);
        }

        return Convert.ToBase64String(buffer.ToArray());
    }

    // Appends SAMLRequest to the IdP URL, keeping any query it already has.
    public static string BuildRedirectUrl(string singleSignOnServiceUrl, string encodedRequest)
    {
        return QueryHelpers.AddQueryString(singleSignOnServiceUrl.Trim(), "SAMLRequest", encodedRequest);
    }
}
