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
}
