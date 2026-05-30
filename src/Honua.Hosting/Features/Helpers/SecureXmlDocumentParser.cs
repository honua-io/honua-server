// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml;
using System.Xml.Linq;

namespace Honua.Server.Features.Infrastructure.Helpers;

/// <summary>
/// Parses XML documents with DTDs and external entity resolution disabled.
/// </summary>
internal static class SecureXmlDocumentParser
{
    public static XDocument Parse(string xml, LoadOptions loadOptions = LoadOptions.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0
        };

        using var stringReader = new StringReader(xml);
        // codeql[cs/xml/missing-validation]: DtdProcessing is Prohibit, XmlResolver is null, and
        // MaxCharactersFromEntities is 0, so DTDs/external entities (XXE) and entity-expansion are
        // blocked before the reader loads.
        // codeql[cs/xml/insecure-dtd-handling]: see above — DTD processing is prohibited.
        using var xmlReader = XmlReader.Create(stringReader, settings);
        return XDocument.Load(xmlReader, loadOptions);
    }
}
