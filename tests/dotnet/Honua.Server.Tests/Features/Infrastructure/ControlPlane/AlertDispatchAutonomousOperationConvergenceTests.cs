// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Alerts;
using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Infrastructure.Monitoring;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class AlertDispatchAutonomousOperationConvergenceTests
{
    [Fact]
    public async Task VerifyAsync_BacklogStaysBelowFindingThreshold_RequiresTwoClearObservations()
    {
        var health = new SequenceDispatchHealth(
            new AlertDispatchBacklog { PendingCount = 2, DeadLetteredCount = 0 },
            new AlertDispatchBacklog { PendingCount = 1, DeadLetteredCount = 0 });
        var sut = CreateSut(health);

        var result = await sut.VerifyAsync(Request(), "opsaction-test");

        result.State.Should().Be(AutonomousVerificationState.Converged);
        result.Message.Should().Contain("remained clear for 2 observations");
        health.RefreshCount.Should().Be(2);
    }

    [Fact]
    public async Task VerifyAsync_DeadLettersReturnDuringObservationWindow_ReturnsFailed()
    {
        var health = new SequenceDispatchHealth(
            new AlertDispatchBacklog { PendingCount = 2, DeadLetteredCount = 0 },
            new AlertDispatchBacklog { PendingCount = 1, DeadLetteredCount = 1 });
        var sut = CreateSut(health);

        var result = await sut.VerifyAsync(Request(), "opsaction-test");

        result.State.Should().Be(AutonomousVerificationState.Failed);
        result.Message.Should().Contain("finding persisted at observation 2 of 2");
        result.Message.Should().Contain("deadLettered=1");
    }

    [Fact]
    public async Task VerifyAsync_LiveBacklogCannotBeRead_ReturnsIndeterminate()
    {
        var health = new SequenceDispatchHealth(new InvalidOperationException("database unavailable"));
        var sut = CreateSut(health);

        var result = await sut.VerifyAsync(Request(), "opsaction-test");

        result.State.Should().Be(AutonomousVerificationState.Indeterminate);
        result.Message.Should().Contain("could not complete (InvalidOperationException)");
        result.Message.Should().NotContain("database unavailable", "provider details must stay out of operator evidence");
    }

    [Fact]
    public async Task CompensateAsync_RedriveAction_ExplicitlyReportsNotSupported()
    {
        var sut = CreateSut(new SequenceDispatchHealth(
            new AlertDispatchBacklog { PendingCount = 0, DeadLetteredCount = 0 }));

        sut.SupportsCompensation(Request()).Should().BeFalse();
        var result = await sut.CompensateAsync(Request(), "opsaction-test");

        result.State.Should().Be(AutonomousCompensationState.NotSupported);
        result.Message.Should().Contain("cannot be safely compensated");
    }

    private static AlertDispatchAutonomousOperationConvergence CreateSut(IAlertDispatchHealth health)
    {
        var options = Substitute.For<IOptionsMonitor<OpsFindingsOptions>>();
        options.CurrentValue.Returns(new OpsFindingsOptions
        {
            AlertDispatchDeadLetterThreshold = 1,
            AlertDispatchPendingBacklogThreshold = 250,
        });
        return new AlertDispatchAutonomousOperationConvergence(health, options, TimeProvider.System);
    }

    private static OperationGatewayRequest Request()
        => new()
        {
            Kind = OperationClass.AdminConfigChange,
            ActionDiscriminator = OpsActionNames.RedriveDeadLetters,
            ExecutionPayload = "{\"action\":\"alerts.redrive_dead_letters\"}",
        };

    private sealed class SequenceDispatchHealth : IAlertDispatchHealth
    {
        private readonly Queue<object> _observations;

        public SequenceDispatchHealth(params object[] observations)
        {
            _observations = new Queue<object>(observations);
        }

        public int RefreshCount { get; private set; }

        public bool IsDispatcherRunning => true;

        public bool IsDispatcherEnabled => true;

        public DateTimeOffset? LastPollAt { get; private set; }

        public AlertDispatchBacklog? LastBacklog { get; private set; }

        public bool IsStoragePollFailing => false;

        public Task<AlertDispatchBacklog> RefreshBacklogAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            var observation = _observations.Count > 1 ? _observations.Dequeue() : _observations.Peek();
            if (observation is Exception exception)
            {
                return Task.FromException<AlertDispatchBacklog>(exception);
            }

            LastBacklog = (AlertDispatchBacklog)observation;
            LastPollAt = DateTimeOffset.UtcNow;
            return Task.FromResult(LastBacklog);
        }
    }
}
