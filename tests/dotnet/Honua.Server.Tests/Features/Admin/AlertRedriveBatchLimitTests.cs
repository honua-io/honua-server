// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

public sealed class AlertRedriveBatchLimitTests
{
    [UnitTheory]
    [InlineData(null, 500)]
    [InlineData(0, 500)]
    [InlineData(-3, 500)]
    [InlineData(120, 120)]
    [InlineData(5000, 5000)]
    [InlineData(int.MaxValue, 5000)]
    public void RequestedBatchIsBoundedByTheMaximum(int? requested, int expected)
        => AlertRedriveBatchLimit.Resolve(requested).Should().Be(expected);
}
