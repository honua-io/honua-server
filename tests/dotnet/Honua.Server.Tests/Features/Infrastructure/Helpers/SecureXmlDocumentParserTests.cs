// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml;
using FluentAssertions;
using Honua.Infrastructure.Helpers;

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
}
