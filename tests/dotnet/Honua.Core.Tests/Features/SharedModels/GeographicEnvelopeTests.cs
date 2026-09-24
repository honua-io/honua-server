// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Core.Tests.Features.SharedModels;

public sealed class GeographicEnvelopeTests
{
    [UnitTest]
    public void TryNormalize_ReversedPacificSpan_CrossesAntimeridian()
    {
        var ok = GeographicEnvelope.TryNormalize(170d, -10d, -170d, 10d, out var normalized, out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        normalized.CrossesAntimeridian.Should().BeTrue();
        normalized.West.Should().Be(170d);
        normalized.East.Should().Be(-170d);
    }

    [UnitTest]
    public void TryNormalize_UnwrappedPacificSpan_FoldsAcrossAntimeridian()
    {
        var ok = GeographicEnvelope.TryNormalize(170d, -10d, 190d, 10d, out var normalized, out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        normalized.CrossesAntimeridian.Should().BeTrue();
        normalized.West.Should().Be(170d);
        normalized.East.Should().Be(-170d);
    }

    [UnitTest]
    public void TryNormalize_OrdinarySpan_StaysOneEnvelope()
    {
        var ok = GeographicEnvelope.TryNormalize(-10d, 40d, 10d, 50d, out var normalized, out _);

        ok.Should().BeTrue();
        normalized.CrossesAntimeridian.Should().BeFalse();
        normalized.West.Should().Be(-10d);
        normalized.East.Should().Be(10d);
    }

    [UnitTest]
    public void TryNormalize_SpanWiderThanTheWorld_IsRejected()
    {
        var ok = GeographicEnvelope.TryNormalize(-200d, -10d, 200d, 10d, out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("360");
    }

    [UnitTheory]
    [InlineData(-180d, 180d)]
    [InlineData(0d, 360d)]
    [InlineData(180d, 540d)]
    public void TryNormalize_FullWorldSpan_UsesCanonicalBounds(double west, double east)
    {
        var ok = GeographicEnvelope.TryNormalize(west, -10d, east, 10d, out var normalized, out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        normalized.Should().Be(new GeographicEnvelope.Normalized(-180d, -10d, 180d, 10d, false));
    }

    [UnitTest]
    public void TryNormalize_ImmediatelyBelowFullWorld_PreservesPartialSpan()
    {
        var ok = GeographicEnvelope.TryNormalize(0d, -10d, Math.BitDecrement(360d), 10d, out var normalized, out _);

        ok.Should().BeTrue();
        normalized.CrossesAntimeridian.Should().BeTrue();
        normalized.West.Should().Be(0d);
        normalized.East.Should().BeLessThan(0d);
    }

    [UnitTest]
    public void TryNormalize_ImmediatelyAboveFullWorld_IsRejected()
    {
        var ok = GeographicEnvelope.TryNormalize(0d, -10d, Math.BitIncrement(360d), 10d, out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("360");
    }
}
