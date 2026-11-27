// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Infrastructure.Helpers;
using Honua.Server.Features.Identity.Saml;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.Helpers;

public sealed class SecureXmlDocumentParserTests
{
    [Fact]
    public void Parse_WithDtd_ThrowsXmlException()
    {
        const string xml = """
            <!DOCTYPE root [
              <!ENTITY xxe SYSTEM "file:///etc/passwd">
            ]>
            <root>&xxe;</root>
            """;

        var act = () => SecureXmlDocumentParser.Parse(xml);

        act.Should().Throw<XmlException>();
    }

    [UnitTest]
    public void Parse_ExcessiveElementDepth_ThrowsXmlException()
    {
        var depth = SecureXmlDocumentParser.DefaultMaxElementDepth + 72;
        var xml = string.Concat(Enumerable.Repeat("<a>", depth)) + string.Concat(Enumerable.Repeat("</a>", depth));

        var act = () => SecureXmlDocumentParser.Parse(xml);

        act.Should().Throw<XmlException>().WithMessage("*nesting depth*");
    }

    [UnitTest]
    public void Parse_WithinMaxElementDepth_Succeeds()
    {
        const int depth = 50;
        var xml = string.Concat(Enumerable.Repeat("<a>", depth)) + string.Concat(Enumerable.Repeat("</a>", depth));

        var document = SecureXmlDocumentParser.Parse(xml);

        document.Root!.Name.LocalName.Should().Be("a");
    }

    [UnitTest]
    public void ExclusiveCanonicalizer_ExcessiveElementDepth_ThrowsXmlException()
    {
        var root = new XElement("a");
        var current = root;
        for (var i = 0; i < ExclusiveCanonicalizer.MaxElementDepth + 20; i++)
        {
            var next = new XElement("a");
            current.Add(next);
            current = next;
        }

        var act = () => ExclusiveCanonicalizer.Canonicalize(root, []);

        act.Should().Throw<XmlException>().WithMessage("*nesting depth*");
    }
}
