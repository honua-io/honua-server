// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.Infrastructure.Helpers;

internal static class SecureXmlDocumentParser
{
    internal const int DefaultMaxElementDepth = RemoteXmlDocumentParser.DefaultMaxElementDepth;
    internal const long DefaultMaxCharactersInDocument = RemoteXmlDocumentParser.DefaultMaxCharactersInDocument;

    public static XDocument Parse(string xml, LoadOptions loadOptions = LoadOptions.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = DefaultMaxCharactersInDocument,
        };

        RemoteXmlDocumentParser.EnsureElementDepthWithinLimit(xml, settings, DefaultMaxElementDepth);

        using var stringReader = new StringReader(xml);
        // codeql[cs/xml/missing-validation]: DtdProcessing is Prohibit, XmlResolver is null,
        // MaxCharactersFromEntities is 0, and element depth and document size are bounded before loading.
        // codeql[cs/xml/insecure-dtd-handling]: see above — DTD processing is prohibited.
        using var xmlReader = XmlReader.Create(stringReader, settings);
        return XDocument.Load(xmlReader, loadOptions);
    }
}
