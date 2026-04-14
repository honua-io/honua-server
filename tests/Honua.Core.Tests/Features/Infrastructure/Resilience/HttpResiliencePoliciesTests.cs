// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Resilience;

namespace Honua.Core.Tests.Features.Infrastructure.Resilience;

/// <summary>
/// Unit tests for <see cref="HttpResiliencePolicies"/>.
/// </summary>
public sealed class HttpResiliencePoliciesTests
{
    [Fact]
    public void GetHttpPolicy_WithEquivalentOptions_ReusesCachedPolicy()
    {
        // Arrange
        var serviceType = $"test-service-{Guid.NewGuid():N}";
        var firstOptions = new ResiliencePolicyOptions
        {
            MaxRetryAttempts = 2,
            BaseDelay = TimeSpan.FromMilliseconds(25),
            BackoffExponent = 1.5,
            JitterPercentage = 0.1,
            CircuitBreakerFailures = 4,
            CircuitBreakDuration = TimeSpan.FromSeconds(5)
        };
        var secondOptions = new ResiliencePolicyOptions
        {
            MaxRetryAttempts = 2,
            BaseDelay = TimeSpan.FromMilliseconds(25),
            BackoffExponent = 1.5,
            JitterPercentage = 0.1,
            CircuitBreakerFailures = 4,
            CircuitBreakDuration = TimeSpan.FromSeconds(5)
        };

        // Act
        var first = HttpResiliencePolicies.GetHttpPolicy(serviceType, firstOptions);
        var second = HttpResiliencePolicies.GetHttpPolicy(serviceType, secondOptions);

        // Assert
        Assert.Same(first, second);
    }
}
