// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.OData.Services;
using Xunit;

namespace Honua.Protocols.OData.Tests.Services;

public sealed class ODataSkipTokenServiceTests
{
    [Fact]
    public void TryDecode_SameQueryDifferentRequestDiscriminator_ReturnsFalse()
    {
        var token = ODataSkipTokenService.Encode(
            offset: 25,
            filter: "ObjectId gt 0",
            orderby: "ObjectId asc",
            requestDiscriminator: "tenant:alpha|subject:user:one");

        var decoded = ODataSkipTokenService.TryDecode(
            token,
            filter: "ObjectId gt 0",
            orderby: "ObjectId asc",
            requestDiscriminator: "tenant:alpha|subject:user:two",
            out _,
            out var error);

        decoded.Should().BeFalse();
        error.Should().Contain("tenant");
    }

    [Fact]
    public void TryDecode_SameQuerySameRequestDiscriminator_ReturnsOffset()
    {
        var token = ODataSkipTokenService.Encode(
            offset: 25,
            filter: "ObjectId gt 0",
            orderby: "ObjectId asc",
            requestDiscriminator: "tenant:alpha|subject:user:one");

        var decoded = ODataSkipTokenService.TryDecode(
            token,
            filter: "ObjectId gt 0",
            orderby: "ObjectId asc",
            requestDiscriminator: "tenant:alpha|subject:user:one",
            out var offset,
            out var error);

        decoded.Should().BeTrue(error);
        offset.Should().Be(25);
    }
}
