// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
}
