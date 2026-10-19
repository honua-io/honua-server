// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Infrastructure.Events.Outbox;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Events.Outbox;

[Protocol(TestProtocols.TestQuality)]
public sealed class OutboxHealthCheckTests
{
    private static readonly HealthCheckContext _context = new()
    {
        Registration = new HealthCheckRegistration("feature-change-outbox", _ => null!, HealthStatus.Degraded, tags: null),
    };

    [UnitTest]
    public async Task CheckHealthAsync_NonCapableProvider_ReturnsHealthyWithLimitation()
    {
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(false);
        capability.CapabilityLimitationDescription.Returns("read-only provider");

        var dispatcherHealth = Substitute.For<IOutboxHealth>();

        var check = new OutboxHealthCheck(capability, dispatcherHealth, BuildOptions());

        var result = await check.CheckHealthAsync(_context);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Be("read-only provider");
    }

    [UnitTest]
    public async Task CheckHealthAsync_DeadLetterAboveThreshold_ReturnsUnhealthy()
    {
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);

        var dispatcherHealth = Substitute.For<IOutboxHealth>();
        dispatcherHealth.IsDispatcherRunning.Returns(true);
        dispatcherHealth.LastBacklog.Returns(new OutboxBacklogMetrics
        {
            PendingCount = 0,
            DeadLetteredCount = 5,
            OldestPendingAgeSeconds = 0,
        });

        var check = new OutboxHealthCheck(capability, dispatcherHealth, BuildOptions(unhealthyDeadLetter: 1));

        var result = await check.CheckHealthAsync(_context);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("dead-lettered");
    }

    [UnitTest]
    public async Task CheckHealthAsync_DispatcherStopped_ReturnsUnhealthy()
    {
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);

        var dispatcherHealth = Substitute.For<IOutboxHealth>();
        dispatcherHealth.IsDispatcherRunning.Returns(false);
        dispatcherHealth.LastBacklog.Returns(new OutboxBacklogMetrics
        {
            PendingCount = 0,
            DeadLetteredCount = 0,
            OldestPendingAgeSeconds = 0,
        });

        var check = new OutboxHealthCheck(capability, dispatcherHealth, BuildOptions());

        var result = await check.CheckHealthAsync(_context);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("not running");
    }

    [UnitTest]
    public async Task CheckHealthAsync_DispatcherStoppedBeforeFirstPass_ReturnsUnhealthy()
    {
        // Cold-start liveness guard (#692): a dispatcher that exits before producing
        // its first backlog snapshot leaves LastBacklog=null, so the readiness check
        // must consult IsDispatcherRunning before the cold-start branch. Otherwise
        // a stopped dispatcher silently reports Healthy and pending events accumulate.
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);

        var dispatcherHealth = Substitute.For<IOutboxHealth>();
        dispatcherHealth.IsDispatcherRunning.Returns(false);
        dispatcherHealth.LastBacklog.Returns((OutboxBacklogMetrics?)null);

        var check = new OutboxHealthCheck(capability, dispatcherHealth, BuildOptions());

        var result = await check.CheckHealthAsync(_context);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("not running");
    }

    [UnitTest]
    public async Task CheckHealthAsync_StoragePollFailingWithoutBacklog_ReturnsUnhealthy()
    {
        // Storage-poll guard (#692): a dispatcher whose claim/recovery/backlog queries
        // throw (missing table, permission issue) reports IsDispatcherRunning=true and
        // LastBacklog=null indefinitely. The previous cold-start Healthy branch hid this
        // from the readiness probe; the dispatcher now sets IsStoragePollFailing on each
        // failure, so health surfaces it as Unhealthy until a poll succeeds.
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);

        var dispatcherHealth = Substitute.For<IOutboxHealth>();
        dispatcherHealth.IsDispatcherRunning.Returns(true);
        dispatcherHealth.LastBacklog.Returns((OutboxBacklogMetrics?)null);
        dispatcherHealth.IsStoragePollFailing.Returns(true);
        dispatcherHealth.LastClaimPollFailureAt.Returns(DateTimeOffset.UtcNow);

        var check = new OutboxHealthCheck(capability, dispatcherHealth, BuildOptions());

        var result = await check.CheckHealthAsync(_context);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("storage polling is failing");
    }

    [UnitTest]
    public async Task CheckHealthAsync_StoragePollFailingWithStaleBacklog_ReturnsDegraded()
    {
        // After a successful pass, intermittent storage-poll failures keep the backlog
        // snapshot but flip the failure flag. Health surfaces this as Degraded so the
        // operator sees the stale snapshot and the failure together rather than a
        // misleading Healthy response.
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);

        var dispatcherHealth = Substitute.For<IOutboxHealth>();
        dispatcherHealth.IsDispatcherRunning.Returns(true);
        dispatcherHealth.LastBacklog.Returns(new OutboxBacklogMetrics
        {
            PendingCount = 3,
            DeadLetteredCount = 0,
            OldestPendingAgeSeconds = 5.0,
        });
        dispatcherHealth.IsStoragePollFailing.Returns(true);
        dispatcherHealth.LastBacklogPollSuccessAt.Returns(DateTimeOffset.UtcNow.AddMinutes(-1));
        dispatcherHealth.LastBacklogPollFailureAt.Returns(DateTimeOffset.UtcNow);

        var check = new OutboxHealthCheck(capability, dispatcherHealth, BuildOptions());

        var result = await check.CheckHealthAsync(_context);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("storage poll failed");
    }

    [UnitTest]
    public async Task CheckHealthAsync_StaleStorageWithDeadLetters_ReturnsUnhealthy()
    {
        // Precedence guard (#692): a known dead-letter snapshot must keep the probe at
        // Unhealthy even when the most recent storage poll failed. Otherwise an
        // intermittent claim/backlog hiccup would demote a row that already requires
        // operator triage from Unhealthy → Degraded, hiding the actionable signal
        // behind the noisier transient-failure signal.
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);

        var dispatcherHealth = Substitute.For<IOutboxHealth>();
        dispatcherHealth.IsDispatcherRunning.Returns(true);
        dispatcherHealth.LastBacklog.Returns(new OutboxBacklogMetrics
        {
            PendingCount = 3,
            DeadLetteredCount = 4,
            OldestPendingAgeSeconds = 12.0,
        });
        // Storage poll is also failing — but dead-letter precedence wins.
        dispatcherHealth.IsStoragePollFailing.Returns(true);
        dispatcherHealth.LastBacklogPollSuccessAt.Returns(DateTimeOffset.UtcNow.AddMinutes(-1));
        dispatcherHealth.LastBacklogPollFailureAt.Returns(DateTimeOffset.UtcNow);

        var check = new OutboxHealthCheck(capability, dispatcherHealth, BuildOptions(unhealthyDeadLetter: 1));

        var result = await check.CheckHealthAsync(_context);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("dead-lettered");
    }

    [UnitTest]
    public async Task CheckHealthAsync_BacklogAboveThreshold_ReturnsDegraded()
    {
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);

        var dispatcherHealth = Substitute.For<IOutboxHealth>();
        dispatcherHealth.IsDispatcherRunning.Returns(true);
        dispatcherHealth.LastBacklog.Returns(new OutboxBacklogMetrics
        {
            PendingCount = 5_000,
            DeadLetteredCount = 0,
            OldestPendingAgeSeconds = 60,
        });

        var check = new OutboxHealthCheck(capability, dispatcherHealth, BuildOptions(degradedBacklog: 1_000));

        var result = await check.CheckHealthAsync(_context);

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [UnitTest]
    public async Task CheckHealthAsync_CleanState_ReturnsHealthy()
    {
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);

        var dispatcherHealth = Substitute.For<IOutboxHealth>();
        dispatcherHealth.IsDispatcherRunning.Returns(true);
        dispatcherHealth.LastBacklog.Returns(new OutboxBacklogMetrics
        {
            PendingCount = 2,
            DeadLetteredCount = 0,
            OldestPendingAgeSeconds = 1.2,
        });

        var check = new OutboxHealthCheck(capability, dispatcherHealth, BuildOptions());

        var result = await check.CheckHealthAsync(_context);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Be("Outbox dispatcher is healthy.");
    }

    private static IOptions<OutboxDispatcherOptions> BuildOptions(
        int degradedBacklog = 1_000,
        int unhealthyDeadLetter = 1)
        => Options.Create(new OutboxDispatcherOptions
        {
            DegradedBacklogThreshold = degradedBacklog,
            UnhealthyDeadLetterThreshold = unhealthyDeadLetter,
        });
}
