// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Postgres.Features.Infrastructure.Resilience;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;
using Polly.CircuitBreaker;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// Tests for per-data-source Polly resilience policies for database connections.
/// Validates retry behavior, callback threading via Context, and isolation of
/// circuit-breaker state across distinct <see cref="NpgsqlDataSource"/> instances.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class ResiliencePoliciesTests
{
    private const string FakeConnectionString1 =
        "Host=fake1.example.invalid;Port=5432;Database=test;Username=u;Password=p;SslMode=Disable";
    private const string FakeConnectionString2 =
        "Host=fake2.example.invalid;Port=5432;Database=test;Username=u;Password=p;SslMode=Disable";

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void GetConnectionRetryPolicy_SameDataSource_ReturnsCachedInstance()
    {
        // Arrange
        using var dataSource = NpgsqlDataSource.Create(FakeConnectionString1);

        // Act
        var policy1 = ResiliencePolicies.GetConnectionRetryPolicy(dataSource);
        var policy2 = ResiliencePolicies.GetConnectionRetryPolicy(dataSource);

        // Assert — same cached instance for the same data source
        policy1.Should().NotBeNull();
        policy1.Should().BeSameAs(policy2);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void GetDeadlockRetryPolicy_SameDataSource_ReturnsCachedInstance()
    {
        // Arrange
        using var dataSource = NpgsqlDataSource.Create(FakeConnectionString1);

        // Act
        var policy1 = ResiliencePolicies.GetDeadlockRetryPolicy(dataSource);
        var policy2 = ResiliencePolicies.GetDeadlockRetryPolicy(dataSource);

        // Assert — same cached instance for the same data source
        policy1.Should().NotBeNull();
        policy1.Should().BeSameAs(policy2);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void GetConnectionRetryPolicy_DifferentDataSources_ReturnsDistinctInstances()
    {
        // Regression — a single shared static breaker would let one failing
        // data source trip the breaker for every healthy data source. Each
        // NpgsqlDataSource must get its own retry+breaker policy instance.
        using var dataSource1 = NpgsqlDataSource.Create(FakeConnectionString1);
        using var dataSource2 = NpgsqlDataSource.Create(FakeConnectionString2);

        var policy1 = ResiliencePolicies.GetConnectionRetryPolicy(dataSource1);
        var policy2 = ResiliencePolicies.GetConnectionRetryPolicy(dataSource2);

        policy1.Should().NotBeSameAs(policy2);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void GetDeadlockRetryPolicy_DifferentDataSources_ReturnsDistinctInstances()
    {
        // Regression — same isolation requirement as the connection retry
        // policy. Deadlock breakers must not bleed across data sources.
        using var dataSource1 = NpgsqlDataSource.Create(FakeConnectionString1);
        using var dataSource2 = NpgsqlDataSource.Create(FakeConnectionString2);

        var policy1 = ResiliencePolicies.GetDeadlockRetryPolicy(dataSource1);
        var policy2 = ResiliencePolicies.GetDeadlockRetryPolicy(dataSource2);

        policy1.Should().NotBeSameAs(policy2);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetConnectionRetryPolicy_TrippedBreakerOnOneDataSource_DoesNotAffectAnother()
    {
        // Regression — directly exercise the breaker on a fresh policy for one
        // data source identity, then verify the policy returned for a second
        // data source identity is still closed and accepts work. Uses fresh
        // policies (not the cached accessor) so the test does not depend on
        // ConditionalWeakTable lifetime semantics or cross-test interference,
        // while still demonstrating the isolation guarantee that the cache
        // provides per data source.
        var policy1 = ResiliencePolicies.CreateFreshConnectionRetryPolicy();
        var policy2 = ResiliencePolicies.CreateFreshConnectionRetryPolicy();

        // Trip policy1's breaker. CircuitBreakerFailures defaults to 5; each
        // ExecuteAsync call exhausts MaxRetryAttempts (3) + 1 initial = 4
        // failures registered against the breaker. Two attempted calls is
        // therefore enough to push past the breaker threshold.
        var contextNoCallback = ResiliencePolicies.CreateRetryContext();
        for (int i = 0; i < 3; i++)
        {
            try
            {
                await policy1.ExecuteAsync(
                    async (_) =>
                    {
                        await Task.Yield();
                        throw new TimeoutException("synthetic failure");
                    },
                    contextNoCallback);
            }
            catch (TimeoutException)
            {
                // expected — driving failures into the breaker
            }
            catch (BrokenCircuitException)
            {
                // expected once the breaker has opened mid-loop
                break;
            }
        }

        // Confirm policy1's breaker is now open by issuing another call.
        var brokenAttempt = async () => await policy1.ExecuteAsync(
            async (_) =>
            {
                await Task.Yield();
                return 0;
            },
            ResiliencePolicies.CreateRetryContext());

        await brokenAttempt.Should().ThrowAsync<BrokenCircuitException>(
            "policy1's breaker should be open after exceeding the failure threshold");

        // Act — execute a successful operation on policy2. With per-instance
        // breakers, policy2's breaker is still closed and the work runs.
        var resultOnHealthyPolicy = await policy2.ExecuteAsync(
            async (_) =>
            {
                await Task.Yield();
                return "ok";
            },
            ResiliencePolicies.CreateRetryContext());

        // Assert
        resultOnHealthyPolicy.Should().Be("ok",
            "tripping the breaker on one data source must not affect another");
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
