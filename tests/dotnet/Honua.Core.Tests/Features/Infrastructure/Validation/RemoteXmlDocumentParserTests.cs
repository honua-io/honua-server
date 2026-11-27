// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Infrastructure.Validation;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Infrastructure.Validation;

public sealed class RemoteXmlDocumentParserTests
{
    [UnitTest]
    public void Parse_WmsCapabilitiesWithDoctype_SkipsTheDoctype()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE WMT_MS_Capabilities SYSTEM "http://schemas.opengis.net/wms/1.1.1/WMS_MS_Capabilities.dtd">
            <WMT_MS_Capabilities version="1.1.1"><Service><Name>OGC:WMS</Name></Service></WMT_MS_Capabilities>
            """;

        var document = RemoteXmlDocumentParser.Parse(xml);

        document.Root!.Name.LocalName.Should().Be("WMT_MS_Capabilities");
        document.DocumentType.Should().BeNull();
    }

    [UnitTest]
    public void Parse_InternalEntityReference_IsNotExpanded()
    {
        const string xml = """
            <!DOCTYPE r [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">]>
            <r>&b;</r>
            """;

        var act = () => RemoteXmlDocumentParser.Parse(xml);

        act.Should().Throw<XmlException>();
    }

    [UnitTest]
    public void Parse_ExternalEntityReference_IsNotResolved()
    {
        const string xml = """
            <!DOCTYPE r [<!ENTITY x SYSTEM "file:///etc/hostname">]>
            <r>&x;</r>
            """;

        var act = () => RemoteXmlDocumentParser.Parse(xml);

        act.Should().Throw<XmlException>();
    }

    [UnitTest]
    public void Parse_OrdinaryDocument_MatchesXDocumentParse()
    {
        const string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<a x=\"1\">\n  <b>&amp;text</b>\n</a>";

        var parsed = RemoteXmlDocumentParser.Parse(xml);

        parsed.ToString(SaveOptions.DisableFormatting)
            .Should().Be(XDocument.Parse(xml).ToString(SaveOptions.DisableFormatting));
        parsed.Declaration!.Encoding.Should().Be("UTF-8");
    }

    [UnitTest]
    public void Parse_MalformedDocument_ThrowsXmlException()
    {
        var act = () => RemoteXmlDocumentParser.Parse("<a><b></a>");

        act.Should().Throw<XmlException>();
    }

    [UnitTest]
    public void Parse_ExcessiveElementDepth_ThrowsXmlException()
    {
        var depth = RemoteXmlDocumentParser.DefaultMaxElementDepth + 72;
        var xml = string.Concat(Enumerable.Repeat("<a>", depth)) + string.Concat(Enumerable.Repeat("</a>", depth));

        var act = () => RemoteXmlDocumentParser.Parse(xml);

        act.Should().Throw<XmlException>().WithMessage("*nesting depth*");
    }

    [UnitTest]
    public void Parse_WithinMaxElementDepth_Succeeds()
    {
        const int depth = 50;
        var xml = string.Concat(Enumerable.Repeat("<a>", depth)) + string.Concat(Enumerable.Repeat("</a>", depth));

        var document = RemoteXmlDocumentParser.Parse(xml);

        document.Root!.Name.LocalName.Should().Be("a");
    }
}
