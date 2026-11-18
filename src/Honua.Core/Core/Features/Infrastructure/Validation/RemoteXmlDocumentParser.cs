// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml;
using System.Xml.Linq;

namespace Honua.Core.Features.Infrastructure.Validation;

// XML from servers or documents Honua does not control. A DOCTYPE is skipped rather than
// refused (WMS 1.1.1 capabilities carry one); nothing it declares is expanded or resolved.
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
