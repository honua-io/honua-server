// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.Ogc.Classic.Wcs20;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wcs20;

public sealed class Wcs20ScalingAndSubsetTests
{
    [UnitTest]
    public void ScaleExtent_WithEqualBounds_SelectsOneCell()
    {
        var query = Query("SCALEEXTENT", "x(5,5),y(0,9)");

        Wcs20Handler.TryResolveScaling(query, 64, 64, out var width, out var height, out _).Should().BeTrue();

        width.Should().Be(1);
        height.Should().Be(10);
    }

    [UnitTest]
    public void ScaleExtent_WithHighBelowLow_IsInvalidExtent()
    {
        var query = Query("SCALEEXTENT", "x(6,5),y(0,9)");

        Wcs20Handler.TryResolveScaling(query, 64, 64, out _, out _, out var error).Should().BeFalse();

        error.ExceptionCode.Should().Be(Wcs20Utilities.ExceptionCodes.InvalidExtent);
    }

    private static QueryCollection Query(string key, string value)
        => new(new Dictionary<string, StringValues> { [key] = value });
}
