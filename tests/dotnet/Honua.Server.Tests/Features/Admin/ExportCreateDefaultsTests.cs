// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Admin.Exports;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

public sealed class ExportCreateDefaultsTests
{
    [UnitTheory]
    [InlineData("Maui County Parcels", "maui-county-parcels")]
    [InlineData("  __Roads (2026)__ ", "roads-2026")]
    [InlineData("é", "export")]
    [InlineData("ab", "export-ab")]
    public void Slug_LayerName_IsAValidExportId(string name, string expected)
    {
        var slug = ExportCreateDefaults.Slug(name, "fallback");

        slug.Should().Be(expected);
        ExportRequestRules.IsExportId(slug).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData(0, 30, 0, 14)]
    [InlineData(20, 22, 14, 14)]
    [InlineData(null, 8, null, 8)]
    [InlineData(2, null, 2, null)]
    [InlineData(-3, 4, 0, 4)]
    public void ClampZoom_StaysInsideTheTileCeiling(int? min, int? max, int? expectedMin, int? expectedMax)
    {
        var zoom = ExportCreateDefaults.ClampZoom(min, max, 0, 14);

        zoom.MinZoom.Should().Be(expectedMin);
        zoom.MaxZoom.Should().Be(expectedMax);
    }
}
