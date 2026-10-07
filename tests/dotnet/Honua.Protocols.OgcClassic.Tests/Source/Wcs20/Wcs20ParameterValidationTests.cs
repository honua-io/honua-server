// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Honua.Protocols.Ogc.Classic.Wcs20;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wcs20;

public sealed class Wcs20ParameterValidationTests
{
    [UnitTest]
    public void Issue5521_GetCoverage_WithToken_DoesNotRejectAuthenticationParameter()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["SERVICE"] = "WCS",
            ["REQUEST"] = "GetCoverage",
            ["VERSION"] = "2.0.1",
            ["COVERAGEID"] = "coverage_4501",
            ["FORMAT"] = "image/tiff",
            ["token"] = "portal-token"
        });

        Wcs20Handler.ValidateGetCoverageParameters(query).Should().BeNull();
    }

    [UnitTheory]
    [InlineData("https://localhost/wcs", "https://localhost/wcs?")]
    [InlineData("https://localhost/wcs?token=abc", "https://localhost/wcs?token=abc&")]
    public void Issue5520_Wcs10Capability_OperationHrefIsReadyForKvpAppending(string endpoint, string expectedHref)
    {
        var capability = Wcs20Handler.BuildWcs10Capability(endpoint);

        var xlinkHref = XName.Get("href", "http://www.w3.org/1999/xlink");
        var hrefs = capability.Descendants()
            .Select(element => element.Attribute(xlinkHref)?.Value)
            .Where(href => href is not null)
            .ToArray();
        hrefs.Should().HaveCount(3).And.OnlyContain(href => href == expectedHref);
    }
}
