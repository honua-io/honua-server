// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.SharedModels;

public sealed class CoordinateSystemEntryTests
{
    [UnitTheory]
    [InlineData("PROJCS[\"Projected\",GEOGCS[\"Geographic\",UNIT[\"degree\",0.017]],UNIT[\"metre\",1]]", "metre", null)]
    [InlineData("GEOGCS[\"Geographic\",UNIT[\"degree\",0.017]]", "degree", null)]
    [InlineData("PROJCRS[\"Projected\",LENGTHUNIT[\"metre\",1],USAGE[AREA[\"New Zealand\"]]]", "metre", "New Zealand")]
    public void FromWkt_ExtractsUnitAndArea(string wkt, string unit, string? area)
    {
        var entry = CoordinateSystemEntry.FromWkt(2193, wkt);
        entry.Should().NotBeNull();
        entry!.Unit.Should().Be(unit);
        entry.Area.Should().Be(area);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("malformed")]
    public void FromWkt_WithoutNamedHead_ReturnsNull(string? wkt)
        => CoordinateSystemEntry.FromWkt(2193, wkt).Should().BeNull();
}
