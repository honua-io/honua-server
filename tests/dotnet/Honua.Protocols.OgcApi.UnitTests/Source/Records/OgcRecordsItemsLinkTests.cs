// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.Ogc.Api.Records;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Records;

/// <summary>Next-page links stay absent when the offset sum does not fit a further page.</summary>
[Protocol(TestProtocols.OgcApiRecords)]
public sealed class OgcRecordsItemsLinkTests
{
    [UnitTest]
    public void BuildItemsLinks_OffsetNearInt32Max_OmitsNextLink()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?offset=2147483000&limit=1000");

        var links = OgcRecordsEndpoints.BuildItemsLinks(
            context,
            "https://example.test",
            "cat",
            numberMatched: 10,
            limit: 1000,
            offset: 2147483000);

        links.Should().NotContain(link => link.Rel == "next");
        links.Select(link => link.Href).Should().NotContain(href => href.Contains("offset=-", StringComparison.Ordinal));
    }

    [UnitTest]
    public void BuildItemsLinks_WithinRange_EmitsTheFollowingOffset()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?offset=0&limit=10");

        var links = OgcRecordsEndpoints.BuildItemsLinks(
            context,
            "https://example.test",
            "cat",
            numberMatched: 25,
            limit: 10,
            offset: 0);

        links.Should().Contain(link => link.Rel == "next" && link.Href.Contains("offset=10", StringComparison.Ordinal));
    }
}
