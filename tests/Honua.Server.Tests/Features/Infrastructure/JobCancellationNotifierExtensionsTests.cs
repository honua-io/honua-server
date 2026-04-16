// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Infrastructure;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure;

/// <summary>
/// Verifies shared fan-out behavior for multi-worker job cancellation.
/// </summary>
[Collection("Unit")]
public sealed class JobCancellationNotifierExtensionsTests
{
    [UnitTest]
    public void CancelAny_WhenLaterNotifierClaimsJob_ReturnsTrue()
    {
        var firstNotifier = Substitute.For<IJobCancellationNotifier>();
        firstNotifier.Cancel("job-1").Returns(false);

        var secondNotifier = Substitute.For<IJobCancellationNotifier>();
        secondNotifier.Cancel("job-1").Returns(true);

        var result = new[] { firstNotifier, secondNotifier }.CancelAny("job-1");

        Assert.True(result);
        firstNotifier.Received(1).Cancel("job-1");
        secondNotifier.Received(1).Cancel("job-1");
    }

    [UnitTest]
    public void CancelAny_WhenNoNotifierClaimsJob_ReturnsFalse()
    {
        var firstNotifier = Substitute.For<IJobCancellationNotifier>();
        var secondNotifier = Substitute.For<IJobCancellationNotifier>();

        var result = new[] { firstNotifier, secondNotifier }.CancelAny("job-1");

        Assert.False(result);
        firstNotifier.Received(1).Cancel("job-1");
        secondNotifier.Received(1).Cancel("job-1");
    }
}
