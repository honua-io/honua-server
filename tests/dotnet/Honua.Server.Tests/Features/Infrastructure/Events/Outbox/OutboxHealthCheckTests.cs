// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Server.Features.Infrastructure.Events.Outbox;
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
