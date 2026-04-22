// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Orchestration.Domain;

namespace Honua.Core.Tests.Features.Orchestration;

public sealed class StepRetryPolicyTests
{
    [Fact]
    public void ComputeDelay_ReturnsZero_ForInitialAttempt()
    {
        var policy = new StepRetryPolicy { MaxAttempts = 3, InitialDelaySeconds = 10 };

        Assert.Equal(TimeSpan.Zero, policy.ComputeDelay(0));
    }

    [Fact]
    public void ComputeDelay_AppliesExponentialBackoff()
    {
        var policy = new StepRetryPolicy
        {
            MaxAttempts = 5,
            InitialDelaySeconds = 10,
            BackoffMultiplier = 2.0
        };

        Assert.Equal(TimeSpan.FromSeconds(10), policy.ComputeDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(20), policy.ComputeDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(40), policy.ComputeDelay(3));
    }

    [Fact]
    public void ComputeDelay_ClampsToMaxDelay()
    {
        var policy = new StepRetryPolicy
        {
            MaxAttempts = 5,
            InitialDelaySeconds = 60,
            BackoffMultiplier = 4.0,
            MaxDelaySeconds = 120
        };

        Assert.Equal(TimeSpan.FromSeconds(120), policy.ComputeDelay(3));
    }
}
