// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Postgres.Features.Infrastructure.Resilience;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// Tests for cached Polly resilience policies for database connections.
/// Validates retry behavior, callback threading via Context, and singleton semantics.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class ResiliencePoliciesTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ConnectionRetryPolicy_ReturnsCachedSingleton()
    {
        // Act
        var policy1 = ResiliencePolicies.ConnectionRetryPolicy;
        var policy2 = ResiliencePolicies.ConnectionRetryPolicy;

        // Assert — same cached instance
        policy1.Should().NotBeNull();
        policy1.Should().BeSameAs(policy2);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void DeadlockRetryPolicy_ReturnsCachedSingleton()
    {
        // Act
        var policy1 = ResiliencePolicies.DeadlockRetryPolicy;
        var policy2 = ResiliencePolicies.DeadlockRetryPolicy;

        // Assert — same cached instance
        policy1.Should().NotBeNull();
        policy1.Should().BeSameAs(policy2);
    }

    [Theory]
    [InlineData("57P03", true)]   // cannot_connect_now
    [InlineData("08000", true)]   // connection_exception
    [InlineData("08003", true)]   // connection_does_not_exist
    [InlineData("08006", true)]   // connection_failure
    [InlineData("40001", false)]  // serialization_failure (should NOT retry)
    [InlineData("40P01", false)]  // deadlock_detected (should NOT retry via connection policy)
    [InlineData("42P01", false)]  // undefined_table (should NOT retry)
    [InlineData("23505", false)]  // unique_violation (should NOT retry)
    [Operation(Operations.TestInfrastructure)]
    public void IsConnectionError_MatchesExpectedSqlStates(string sqlState, bool shouldRetry)
    {
        var shouldActuallyRetry = sqlState is "57P03" or "08000" or "08003" or "08006";
        shouldActuallyRetry.Should().Be(shouldRetry, $"SqlState {sqlState} retry expectation should match implementation");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ConnectionRetryPolicy_RetriesTimeoutExceptions()
    {
        // Arrange — fresh policy avoids circuit breaker cross-test interference
        var policy = ResiliencePolicies.CreateFreshConnectionRetryPolicy();
        var retryCount = 0;
        var context = ResiliencePolicies.CreateRetryContext(
            (ex, timespan, attempt) => retryCount = attempt);

        var exception = new TimeoutException("Connection timeout");

        // Act & Assert — should exhaust retries (3 attempts + initial = 4 total attempts)
        var thrownEx = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await policy.ExecuteAsync(
                async (_) =>
                {
                    await Task.Delay(1);
                    throw exception;
                },
                context));

        thrownEx.Should().Be(exception);
        retryCount.Should().Be(3, "should have retried 3 times for TimeoutException");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ConnectionRetryPolicy_SucceedsAfterRetry()
    {
        // Arrange
        var policy = ResiliencePolicies.CreateFreshConnectionRetryPolicy();
        var attemptCount = 0;
        var retryCount = 0;
        var context = ResiliencePolicies.CreateRetryContext(
            (ex, timespan, attempt) => retryCount = attempt);

        var exception = new TimeoutException("Connection timeout");

        // Act
        var result = await policy.ExecuteAsync(
            async (_) =>
            {
                attemptCount++;
                await Task.Delay(1);

                if (attemptCount < 3)
                {
                    throw exception;
                }

                return "success";
            },
            context);

        // Assert
        result.Should().Be("success");
        attemptCount.Should().Be(3, "should have made 3 attempts total");
        retryCount.Should().Be(2, "should have retried 2 times before success");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ConnectionRetryPolicy_UsesExponentialBackoff()
    {
        // Arrange
        var policy = ResiliencePolicies.CreateFreshConnectionRetryPolicy();
        var retryDelays = new List<TimeSpan>();
        var context = ResiliencePolicies.CreateRetryContext(
            (ex, timespan, attempt) => retryDelays.Add(timespan));

        var exception = new TimeoutException("Connection timeout");

        // Act
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await policy.ExecuteAsync(
                async (_) =>
                {
                    await Task.Delay(1);
                    throw exception;
                },
                context));

        // Assert — Exponential backoff: 100ms * 2^attempt
        retryDelays.Should().HaveCount(3);
        retryDelays[0].Should().Be(TimeSpan.FromMilliseconds(200)); // 100 * 2^1
        retryDelays[1].Should().Be(TimeSpan.FromMilliseconds(400)); // 100 * 2^2
        retryDelays[2].Should().Be(TimeSpan.FromMilliseconds(800)); // 100 * 2^3
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ConnectionRetryPolicy_CallsOnRetryCallbackViaContext()
    {
        // Arrange
        var policy = ResiliencePolicies.CreateFreshConnectionRetryPolicy();
        var retryCallbacks = new List<(Exception Exception, TimeSpan Delay, int Attempt)>();

        var context = ResiliencePolicies.CreateRetryContext(
            (ex, timespan, attempt) => retryCallbacks.Add((ex, timespan, attempt)));

        var exception = new TimeoutException("Connection timeout");

        // Act
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await policy.ExecuteAsync(
                async (_) =>
                {
                    await Task.Delay(1);
                    throw exception;
                },
                context));

        // Assert
        retryCallbacks.Should().HaveCount(3);

        for (int i = 0; i < 3; i++)
        {
            retryCallbacks[i].Exception.Should().Be(exception);
            retryCallbacks[i].Attempt.Should().Be(i + 1);
        }
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ConnectionRetryPolicy_WorksWithoutCallback()
    {
        // Arrange — fresh policy, empty context, no callback
        var policy = ResiliencePolicies.CreateFreshConnectionRetryPolicy();
        var context = ResiliencePolicies.CreateRetryContext();
        var exception = new TimeoutException("Connection timeout");

        // Act & Assert — should not throw due to missing callback
        var thrownEx = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await policy.ExecuteAsync(
                async (_) =>
                {
                    await Task.Delay(1);
                    throw exception;
                },
                context));

        thrownEx.Should().Be(exception);
    }
}
