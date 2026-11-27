// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml;
using System.Xml.Linq;

namespace Honua.Core.Features.Infrastructure.Validation;

// XML from servers or documents Honua does not control. A DOCTYPE is skipped rather than
// refused (WMS 1.1.1 capabilities carry one); nothing it declares is expanded or resolved.
// Documents Honua parses for authentication (SAML) use SecureXmlDocumentParser in Honua.Hosting,
// which refuses a DOCTYPE outright.
public static class RemoteXmlDocumentParser
{
    public const int DefaultMaxElementDepth = 128;

    public const long DefaultMaxCharactersInDocument = 10_000_000;

    public static XDocument Parse(string xml, LoadOptions loadOptions = LoadOptions.None)
    {
        ArgumentNullException.ThrowIfNull(xml);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
            MaxCharactersInDocument = DefaultMaxCharactersInDocument,
            // Same whitespace handling as XDocument.Parse for the given options.
            IgnoreWhitespace = (loadOptions & LoadOptions.PreserveWhitespace) == 0,
        };

        EnsureElementDepthWithinLimit(xml, settings, DefaultMaxElementDepth);

        using var stringReader = new StringReader(xml);
        // codeql[cs/xml/missing-validation]: DTDs are ignored, so no entity is declared or expanded,
        // XmlResolver is null, and element depth and document size are bounded before loading.
        using var xmlReader = XmlReader.Create(stringReader, settings);
        return XDocument.Load(xmlReader, loadOptions);
    }

    public static XDocument ParseProhibitingDtd(string xml, LoadOptions loadOptions = LoadOptions.None)
    {
        ArgumentNullException.ThrowIfNull(xml);

        var settings = CreateProhibitingDtdSettings();
        EnsureElementDepthWithinLimit(xml, settings, DefaultMaxElementDepth);

        using var stringReader = new StringReader(xml);
        // codeql[cs/xml/missing-validation]: DtdProcessing is Prohibit, XmlResolver is null,
        // MaxCharactersFromEntities is 0, and element depth and document size are bounded before loading.
        // codeql[cs/xml/insecure-dtd-handling]: DTD processing is prohibited.
        using var xmlReader = XmlReader.Create(stringReader, settings);
        return XDocument.Load(xmlReader, loadOptions);
    }

    public static bool ExceedsElementDepthLimit(string xml, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);

        try
        {
            return ContainsElementAtOrAboveDepth(xml, CreateProhibitingDtdSettings(), maxDepth);
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static XmlReaderSettings CreateProhibitingDtdSettings()
        => new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = DefaultMaxCharactersInDocument,
        };

    private static bool ContainsElementAtOrAboveDepth(string xml, XmlReaderSettings settings, int maxDepth)
    {
        using var probeStringReader = new StringReader(xml);
        // codeql[cs/xml/missing-validation]: DtdProcessing is Prohibit and XmlResolver is null.
        // codeql[cs/xml/insecure-dtd-handling]: DTD processing is prohibited.
        using var probeReader = XmlReader.Create(probeStringReader, settings);
        while (probeReader.Read())
        {
            if (probeReader.Depth >= maxDepth)
            {
                return true;
            }
        }

        return false;
    }

    // Streams through the XML document without building a DOM to reject excessive nesting depth before recursive parsing.
    public static void EnsureElementDepthWithinLimit(string xml, XmlReaderSettings settings, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);

        using var probeStringReader = new StringReader(xml);
        // codeql[cs/xml/missing-validation]: caller supplies hardened XmlReaderSettings with DtdProcessing restricted and XmlResolver null.
        // codeql[cs/xml/insecure-dtd-handling]: caller supplies hardened XmlReaderSettings with DtdProcessing restricted.
        using var probeReader = XmlReader.Create(probeStringReader, settings);
        while (probeReader.Read())
        {
            if (probeReader.Depth >= maxDepth)
            {
                throw new XmlException($"XML element nesting depth exceeds the maximum allowed depth of {maxDepth}.");
            }
        }
    }
}
