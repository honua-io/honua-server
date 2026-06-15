// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Plugins.Tests;

public sealed class PluginCircuitBreakerTests
{
    private const string Plugin = "p1";

    [Fact]
    public void ClosedByDefault_AllowsAndReportsNotOpen()
    {
        var breaker = new PluginCircuitBreaker();
        breaker.IsAllowed(Plugin).Should().BeTrue();
        breaker.IsOpen(Plugin).Should().BeFalse();
    }

    [Fact]
    public void TripsOpen_AfterThresholdConsecutiveFailures()
    {
        var breaker = new PluginCircuitBreaker(failureThreshold: 3);

        breaker.RecordFailure(Plugin).Should().BeFalse();
        breaker.RecordFailure(Plugin).Should().BeFalse();
        breaker.RecordFailure(Plugin).Should().BeTrue("the third consecutive failure trips the breaker open");

        breaker.IsOpen(Plugin).Should().BeTrue();
        breaker.IsAllowed(Plugin).Should().BeFalse("an open breaker blocks until the cooldown elapses");
    }

    [Fact]
    public void Trip_IsReportedOnlyOnce_OnClosedToOpenTransition()
    {
        var breaker = new PluginCircuitBreaker(failureThreshold: 2);
        breaker.RecordFailure(Plugin);
        breaker.RecordFailure(Plugin).Should().BeTrue();

        // Already open: further failures must not re-report the transition.
        breaker.RecordFailure(Plugin).Should().BeFalse();
    }

    [Fact]
    public void Success_ResetsFailures_AndReportsRecoveryWhenWasOpen()
    {
        var breaker = new PluginCircuitBreaker(failureThreshold: 2);
        breaker.RecordFailure(Plugin);
        breaker.RecordFailure(Plugin).Should().BeTrue();

        breaker.RecordSuccess(Plugin).Should().BeTrue("recovering a previously-open breaker reports the transition");
        breaker.IsOpen(Plugin).Should().BeFalse();
        breaker.IsAllowed(Plugin).Should().BeTrue();

        // A success on an already-closed breaker is not a recovery transition.
        breaker.RecordSuccess(Plugin).Should().BeFalse();
    }

    [Fact]
    public void IntermittentSuccess_ResetsTheConsecutiveCount()
    {
        var breaker = new PluginCircuitBreaker(failureThreshold: 3);
        breaker.RecordFailure(Plugin);
        breaker.RecordFailure(Plugin);
        breaker.RecordSuccess(Plugin);

        // Count reset: two fresh failures must not trip (would need a third).
        breaker.RecordFailure(Plugin).Should().BeFalse();
        breaker.RecordFailure(Plugin).Should().BeFalse();
        breaker.IsOpen(Plugin).Should().BeFalse();
    }

    [Fact]
    public void HalfOpen_AllowsRetry_AfterCooldownElapses()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero));
        var breaker = new PluginCircuitBreaker(failureThreshold: 1, cooldown: TimeSpan.FromMinutes(5), timeProvider: time);

        breaker.RecordFailure(Plugin).Should().BeTrue();
        breaker.IsAllowed(Plugin).Should().BeFalse("still within cooldown");

        time.Advance(TimeSpan.FromMinutes(5));
        breaker.IsAllowed(Plugin).Should().BeTrue("cooldown elapsed: a half-open trial is permitted");
    }

    [Fact]
    public void IsolatesPerPlugin()
    {
        var breaker = new PluginCircuitBreaker(failureThreshold: 1);
        breaker.RecordFailure("a");

        breaker.IsAllowed("a").Should().BeFalse();
        breaker.IsAllowed("b").Should().BeTrue("a different plugin's breaker is unaffected");
    }

    [Fact]
    public void Ctor_RejectsNonPositiveThreshold()
    {
        var act = () => new PluginCircuitBreaker(failureThreshold: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>Minimal advanceable <see cref="TimeProvider"/> (the suite defines its own per-project).</summary>
    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
