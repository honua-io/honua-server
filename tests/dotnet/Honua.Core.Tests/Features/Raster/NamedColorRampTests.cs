// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Raster;

public sealed class NamedColorRampTests
{
    [UnitTest]
    public void TryResolve_KnownName_ProducesStopsSpanningDisplayRange()
    {
        NamedColorRamp.TryResolve("Elevation", out var colormap).Should().BeTrue();

        colormap.Entries.Should().HaveCountGreaterThan(1);
        colormap.Entries[0].Value.Should().Be(0);
        colormap.Entries[^1].Value.Should().Be(255);
        colormap.Entries.Should().BeInAscendingOrder(static e => e.Value);
    }

    [UnitTest]
    public void TryResolve_IsCaseInsensitiveAndTrimsWhitespace()
    {
        NamedColorRamp.TryResolve("  red TO green ".Trim(), out var colormap).Should().BeTrue();
        colormap.Entries.Should().NotBeEmpty();
    }

    [UnitTest]
    public void TryResolve_UnknownName_ReturnsFalse()
    {
        NamedColorRamp.TryResolve("Not A Real Ramp", out _).Should().BeFalse();
    }

    [UnitTest]
    public void TryResolve_NullOrEmpty_ReturnsFalse()
    {
        NamedColorRamp.TryResolve(null, out _).Should().BeFalse();
        NamedColorRamp.TryResolve("   ", out _).Should().BeFalse();
    }

    [UnitTest]
    public void SingleHueRamp_HasOpaqueEndpoints()
    {
        NamedColorRamp.TryResolve("Black to White", out var colormap).Should().BeTrue();

        colormap.Entries.Should().HaveCount(2);
        colormap.Entries[0].Should().Be(new RasterColormapEntry(0, 0, 0, 0, 255));
        colormap.Entries[1].Should().Be(new RasterColormapEntry(255, 255, 255, 255, 255));
    }
}
