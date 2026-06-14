// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Sockets;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Infrastructure.Resilience;

/// <summary>
/// Unit tests for <see cref="StartupDatabaseResilience"/> — the degraded-start classifier and
/// backoff used to keep a serverless host alive when the database is unavailable at boot (#1632).
/// </summary>
public sealed class StartupDatabaseResilienceTests
{
    [UnitTest]
    public void IsTransientConnectivityError_WithTimeoutException_ReturnsTrue()
    {
        StartupDatabaseResilience.IsTransientConnectivityError(new TimeoutException())
            .Should().BeTrue();
    }

    [UnitTest]
    public void IsTransientConnectivityError_WithSocketException_ReturnsTrue()
    {
        StartupDatabaseResilience.IsTransientConnectivityError(new SocketException())
            .Should().BeTrue();
    }

    [UnitTest]
    public void IsTransientConnectivityError_WithSocketExceptionNestedInInnerChain_ReturnsTrue()
    {
        var nested = new InvalidOperationException(
            "connect failed", new IOException("broken pipe", new SocketException()));

        StartupDatabaseResilience.IsTransientConnectivityError(nested).Should().BeTrue();
    }

    [UnitTest]
    public void IsTransientConnectivityError_WithMigrationSqlError_ReturnsFalse()
    {
        // A bad migration script (e.g. syntax error) is not transient and must still fail loudly.
        StartupDatabaseResilience.IsTransientConnectivityError(
            new InvalidOperationException("syntax error at or near \"CREAT\""))
            .Should().BeFalse();
    }

    [UnitTest]
    public void IsTransientConnectivityError_WithNull_ReturnsFalse()
    {
        StartupDatabaseResilience.IsTransientConnectivityError(null).Should().BeFalse();
    }

    [UnitTest]
    public void IsTransientConnectivityError_WithAggregateOfAllTransient_ReturnsTrue()
    {
        var aggregate = new AggregateException(new TimeoutException(), new SocketException());
        StartupDatabaseResilience.IsTransientConnectivityError(aggregate).Should().BeTrue();
    }

    [UnitTest]
    public void IsTransientConnectivityError_WithAggregateContainingPermanentFailure_ReturnsFalse()
    {
        var aggregate = new AggregateException(
            new TimeoutException(),
            new InvalidOperationException("relation already exists"));

        StartupDatabaseResilience.IsTransientConnectivityError(aggregate).Should().BeFalse();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("53300", true)]  // too_many_connections (pool/slot exhaustion)
    [InlineData("53400", true)]  // configuration_limit_exceeded
    [InlineData("57P03", true)]  // cannot_connect_now (server starting up)
    [InlineData("08006", true)]  // connection_failure (class 08)
    [InlineData("08001", true)]  // sqlclient_unable_to_establish_sqlconnection
    [InlineData("42601", false)] // syntax_error (permanent)
    [InlineData("42P07", false)] // duplicate_table (permanent)
    [InlineData("23505", false)] // unique_violation (permanent)
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTransientSqlState_ClassifiesCorrectly(string? sqlState, bool expected)
    {
        StartupDatabaseResilience.IsTransientSqlState(sqlState).Should().Be(expected);
    }

    [UnitTest]
    public void GetRetryDelay_GrowsExponentiallyAndIsCapped()
    {
        var baseDelay = TimeSpan.FromSeconds(2);
        var maxDelay = TimeSpan.FromSeconds(60);

        // No jitter -> deterministic exponential growth.
        StartupDatabaseResilience.GetRetryDelay(1, baseDelay, maxDelay)
            .Should().Be(TimeSpan.FromSeconds(2));
        StartupDatabaseResilience.GetRetryDelay(2, baseDelay, maxDelay)
            .Should().Be(TimeSpan.FromSeconds(4));
        StartupDatabaseResilience.GetRetryDelay(3, baseDelay, maxDelay)
            .Should().Be(TimeSpan.FromSeconds(8));

        // Large attempt is clamped to the ceiling.
        StartupDatabaseResilience.GetRetryDelay(20, baseDelay, maxDelay)
            .Should().Be(maxDelay);
    }

    [UnitTest]
    public void GetRetryDelay_WithJitter_StaysWithinCappedBound()
    {
        var baseDelay = TimeSpan.FromSeconds(2);
        var maxDelay = TimeSpan.FromSeconds(60);
        var jitter = new Random(12345);

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var delay = StartupDatabaseResilience.GetRetryDelay(attempt, baseDelay, maxDelay, jitter);
            delay.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
            delay.Should().BeLessThanOrEqualTo(maxDelay);
        }
    }

    [UnitTest]
    public void GetRetryDelay_WithAttemptBelowOne_TreatedAsFirstAttempt()
    {
        var baseDelay = TimeSpan.FromSeconds(2);
        var maxDelay = TimeSpan.FromSeconds(60);

        StartupDatabaseResilience.GetRetryDelay(0, baseDelay, maxDelay)
            .Should().Be(TimeSpan.FromSeconds(2));
    }

    [UnitTest]
    public void BuildDegradedStartWarning_IncludesPhaseAndGuidance()
    {
        var warning = StartupDatabaseResilience.BuildDegradedStartWarning(
            "migrations", "connection refused");

        warning.Should().Contain("migrations");
        warning.Should().Contain("connection refused");
        warning.Should().Contain("degraded-start");
        warning.Should().Contain("/healthz/ready");
    }
}
