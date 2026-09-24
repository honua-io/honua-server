// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Xunit;

namespace Honua.Core.Tests.Features.SharedModels;

public sealed class GeographicEnvelopeTests
{
    [Fact]
    public void TryNormalize_ReversedPacificSpan_CrossesAntimeridian()
    {
        var ok = GeographicEnvelope.TryNormalize(170d, -10d, -170d, 10d, out var normalized, out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        normalized.CrossesAntimeridian.Should().BeTrue();
        normalized.West.Should().Be(170d);
        normalized.East.Should().Be(-170d);
    }

    [Fact]
    public void TryNormalize_UnwrappedPacificSpan_FoldsAcrossAntimeridian()
    {
        var ok = GeographicEnvelope.TryNormalize(170d, -10d, 190d, 10d, out var normalized, out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        normalized.CrossesAntimeridian.Should().BeTrue();
        normalized.West.Should().Be(170d);
        normalized.East.Should().Be(-170d);
    }

    [Fact]
    public void TryNormalize_OrdinarySpan_StaysOneEnvelope()
    {
        var ok = GeographicEnvelope.TryNormalize(-10d, 40d, 10d, 50d, out var normalized, out _);

        ok.Should().BeTrue();
        normalized.CrossesAntimeridian.Should().BeFalse();
        normalized.West.Should().Be(-10d);
        normalized.East.Should().Be(10d);
    }

    [Fact]
    public void TryNormalize_SpanWiderThanTheWorld_IsRejected()
    {
        var ok = GeographicEnvelope.TryNormalize(-200d, -10d, 200d, 10d, out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("360");
    }
}
