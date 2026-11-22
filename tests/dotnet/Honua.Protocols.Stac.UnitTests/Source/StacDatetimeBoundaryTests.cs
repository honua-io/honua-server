// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.Stac.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Stac;

public sealed class StacDatetimeBoundaryTests
{
    [UnitTheory]
    [InlineData("2023-01-02T00:00Z")]
    [InlineData("2023-01-02T00:00+01:00")]
    [InlineData("2023-01-02T00:00.123Z")]
    [InlineData("../2023-01-02T00:00Z")]
    [InlineData("2023-01-02T00:00Z/..")]
    [InlineData("2023-01-02T0:00:00Z")]
    [InlineData("2023-01-02T00:0:00Z")]
    [InlineData("2023-01-02T00:00:0Z")]
    [InlineData("2023-01-02T00:00:00.Z")]
    public void DatetimeSyntax_WithIncompleteTime_IsInvalid(string datetime)
    {
        StacFilterHelpers.IsValidDatetimeSyntax(datetime).Should().BeFalse(datetime);
    }

    [UnitTheory]
    [InlineData("2023-01-02T00:00:00Z")]
    [InlineData("2023-01-02t00:00:00z")]
    [InlineData("2023-01-02T00:00:00.123456789Z")]
    [InlineData("2023-01-02T00:00:00+01:00")]
    [InlineData("/2023-01-02T00:00:00Z")]
    [InlineData("2023-01-02T00:00:00Z/")]
    [InlineData("../2023-01-02T00:00:00Z")]
    [InlineData("2023-01-02T00:00:00Z/..")]
    public void DatetimeSyntax_WithCompleteTime_IsValid(string datetime)
    {
        StacFilterHelpers.IsValidDatetimeSyntax(datetime).Should().BeTrue(datetime);
    }
}
