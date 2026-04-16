// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;

namespace Honua.Server.Tests.Infrastructure;

public class PostgresFixtureTests
{
    [Fact]
    public async Task ExecuteWithInitializationRetryAsync_WhenTransientFailuresRecover_RetriesUntilSuccess()
    {
        var attempts = 0;

        await PostgresFixture.ExecuteWithInitializationRetryAsync(
            () =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new TimeoutException("container still starting");
                }

                return Task.CompletedTask;
            },
            maxAttempts: 3,
            baseDelay: TimeSpan.Zero);

        attempts.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteWithInitializationRetryAsync_WhenTransientFailuresExhausted_ThrowsWrappedFailure()
    {
        var attempts = 0;

        var act = async () => await PostgresFixture.ExecuteWithInitializationRetryAsync(
            () =>
            {
                attempts++;
                throw new TimeoutException("container still starting");
            },
            maxAttempts: 3,
            baseDelay: TimeSpan.Zero);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.InnerException.Should().BeOfType<TimeoutException>();
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteWithInitializationRetryAsync_WhenFailureIsNonTransient_DoesNotRetry()
    {
        var attempts = 0;

        var act = async () => await PostgresFixture.ExecuteWithInitializationRetryAsync(
            () =>
            {
                attempts++;
                throw new InvalidOperationException("invalid startup state");
            },
            maxAttempts: 5,
            baseDelay: TimeSpan.Zero);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("invalid startup state");
        attempts.Should().Be(1);
    }
}
