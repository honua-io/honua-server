// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// Unit tests for the per-connection default-subscription delivery fence (honua-server#6104).
/// </summary>
public sealed class FeatureStreamDeliveredCursorWindowTests
{
    [UnitTest]
    public void TracksOutOfOrderAndDuplicatesWithinBoundedMemory()
    {
        var window = new FeatureStreamDeliveredCursorWindow(floor: 100, capacity: 3);

        window.IsHandled(100).Should().BeTrue("cursors at or below the swept floor were already swept");
        window.IsHandled(106).Should().BeFalse();
        window.MarkDelivered(106);
        window.IsHandled(106).Should().BeTrue("a re-broadcast of a delivered cursor is a duplicate");
        window.IsHandled(105).Should().BeFalse("a lower cursor that was never delivered must still be sent");
        window.MarkDelivered(105);
        window.IsHandled(105).Should().BeTrue();

        window.MarkDelivered(107);
        window.MarkDelivered(108);
        window.Count.Should().Be(3, "memory stays bounded by capacity");
        window.IsHandled(105).Should().BeFalse("the oldest entry is forgotten, which may re-send but never drops");

        window.AdvanceFloor(107);
        window.Floor.Should().Be(107);
        window.Count.Should().Be(1);
        window.IsHandled(103).Should().BeTrue();
        window.IsHandled(108).Should().BeTrue();
        window.AdvanceFloor(50);
        window.Floor.Should().Be(107, "the floor never moves backwards");
        window.IsHandled(0).Should().BeFalse("frames without a durable cursor are never fenced");
    }
}
