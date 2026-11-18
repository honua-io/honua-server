// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml;
using System.Xml.Linq;

namespace Honua.Core.Features.Infrastructure.Validation;

/// <summary>
/// Parses XML that came from a server or a document Honua does not control (capabilities,
/// schemas, coverage descriptions, styles). A DOCTYPE is skipped rather than refused, because
/// WMS 1.1.1 capabilities routinely carry one; no entity it declares is expanded and no
/// external resource is resolved. Malformed input, including a reference to an entity the
/// skipped DOCTYPE declared, throws <see cref="XmlException"/> exactly as XDocument.Parse does.
/// </summary>
public static class RemoteXmlDocumentParser
{
    public static XDocument Parse(string xml, LoadOptions loadOptions = LoadOptions.None)
    {
        ArgumentNullException.ThrowIfNull(xml);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            // Same whitespace handling as XDocument.Parse for the given options.
            IgnoreWhitespace = (loadOptions & LoadOptions.PreserveWhitespace) == 0,
        };

        using var stringReader = new StringReader(xml);
        // codeql[cs/xml/missing-validation]: DTDs are ignored, XmlResolver is null and
        // MaxCharactersFromEntities is 0, so external entities and entity expansion are blocked.
        using var xmlReader = XmlReader.Create(stringReader, settings);
        return XDocument.Load(xmlReader, loadOptions);
    }
}
