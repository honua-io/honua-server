// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Alerts;
using Honua.Alerts.Ops;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Honua.Server.Tests.Infrastructure.Telemetry;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class OpsNotificationServiceTests
{
    [UnitTest]
    public async Task NotifyAsync_WhenEnabled_EnqueuesOpsEventToConfiguredChannel()
    {
        var outbox = Substitute.For<IAlertOutboxWriter>();

        var sut = Create(outbox, out _, out _, enabled: true, channels: ["webhook"]);

        await sut.NotifyAsync(Notification(AlertSeverity.Critical), CancellationToken.None);

        // Ops event appended AND its dispatch enqueued atomically, with the ops source discriminator.
        await outbox.Received(1).CommitEvaluationAsync(
            Arg.Is<IReadOnlyCollection<AlertStateSnapshot>>(states => states.Count == 0),
            Arg.Is<IReadOnlyList<AlertOutboxEntry>>(entries => entries.Count == 1 &&
                entries[0].AlertEvent.Source == AlertEventSources.Ops &&
                entries[0].AlertEvent.RuleId == 0 &&
                entries[0].AlertEvent.DedupeKey == "ops:deploy-workflow:op-1:Failed" &&
                entries[0].Channels.Contains(AlertChannelType.Webhook)),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task NotifyAsync_WhenDisabled_DoesNotEnqueue()
    {
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var sut = Create(outbox, out _, out _, enabled: false, channels: ["webhook"]);

        await sut.NotifyAsync(Notification(AlertSeverity.Critical), CancellationToken.None);

        await outbox.DidNotReceive().AppendAndEnqueueAsync(
            Arg.Any<AlertEventEnvelope>(), Arg.Any<ImmutableArray<AlertChannelType>>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task NotifyAsync_BelowMinSeverity_DoesNotEnqueue()
    {
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var sut = Create(outbox, out _, out _, enabled: true, channels: ["webhook"], minSeverity: AlertSeverity.Warning);

        await sut.NotifyAsync(Notification(AlertSeverity.Info), CancellationToken.None);

        await outbox.DidNotReceive().AppendAndEnqueueAsync(
            Arg.Any<AlertEventEnvelope>(), Arg.Any<ImmutableArray<AlertChannelType>>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task NotifyAsync_EditionGating_DropsDisallowedChannels()
    {
        var outbox = Substitute.For<IAlertOutboxWriter>();

        var sut = Create(
            outbox,
            out var editionPolicy,
            out _,
            enabled: true,
            channels: ["webhook", "slack"]);

        // Pro-like edition: webhook allowed, slack (rich channel) not.
        editionPolicy.IsChannelAllowed(AlertChannelType.Webhook).Returns(true);
        editionPolicy.IsChannelAllowed(AlertChannelType.Slack).Returns(false);

        await sut.NotifyAsync(Notification(AlertSeverity.Critical), CancellationToken.None);

        await outbox.Received(1).CommitEvaluationAsync(
            Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
            Arg.Is<IReadOnlyList<AlertOutboxEntry>>(entries => entries.Count == 1 &&
                entries[0].Channels.Contains(AlertChannelType.Webhook) &&
                !entries[0].Channels.Contains(AlertChannelType.Slack)),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task NotifyAsync_WhenChannelCircuitOpen_PersistsEvidenceWithoutDispatch()
    {
        var outbox = Substitute.For<IAlertOutboxWriter>();

        // Threshold 1 so a single dead-letter trips the breaker for the webhook channel.
        var sut = Create(outbox, out _, out var breaker, enabled: true, channels: ["webhook"], circuitThreshold: 1);
        breaker.RecordDeadLetter(AlertChannelType.Webhook, DateTimeOffset.UtcNow);

        await sut.NotifyAsync(Notification(AlertSeverity.Critical), CancellationToken.None);

        // The tripped channel gets no dispatch row (bounded dead-letter volume), but the ops event
        // remains durable so operators can still reconstruct what the autonomous system did.
        await outbox.Received(1).CommitEvaluationAsync(
            Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
            Arg.Is<IReadOnlyList<AlertOutboxEntry>>(entries => entries.Count == 1 &&
                entries[0].AlertEvent.Source == AlertEventSources.Ops &&
                entries[0].Channels.IsEmpty),
            Arg.Any<CancellationToken>());
    }

    private static OpsNotification Notification(AlertSeverity severity)
        => new()
        {
            Source = "deploy-workflow",
            Severity = severity,
            Title = "Deploy Failed: op-1",
            Body = "Deploy operation 'op-1' reached terminal status Failed.",
            DedupeIdentifier = "op-1:Failed",
        };

    private static OpsNotificationService Create(
        IAlertOutboxWriter outbox,
        out IAlertEditionPolicy editionPolicy,
        out AlertChannelCircuitBreaker breaker,
        bool enabled,
        IReadOnlyList<string> channels,
        AlertSeverity minSeverity = AlertSeverity.Info,
        int circuitThreshold = 5)
    {
        outbox.CommitEvaluationAsync(
                Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
                Arg.Any<IReadOnlyList<AlertOutboxEntry>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => ImmutableArray.CreateRange(
                Enumerable.Repeat(true, call.ArgAt<IReadOnlyList<AlertOutboxEntry>>(1).Count)));

        var writer = new AlertDispatchWriter(outbox, TestTelemetry.CreateAlertPipelineMetrics(), NullLogger<AlertDispatchWriter>.Instance);
        editionPolicy = Substitute.For<IAlertEditionPolicy>();
        editionPolicy.IsChannelAllowed(Arg.Any<AlertChannelType>()).Returns(true);

        var options = Options.Create(new AlertOptions
        {
            Enabled = true,
            Ops = new AlertOpsOptions
            {
                Enabled = enabled,
                Channels = channels,
                MinSeverity = minSeverity,
            },
            Dispatch = new AlertDispatchOptions
            {
                CircuitBreakerThreshold = circuitThreshold,
            },
        });

        breaker = new AlertChannelCircuitBreaker(options);
        return new OpsNotificationService(writer, editionPolicy, breaker, options, TestTelemetry.CreateAlertPipelineMetrics(), NullLogger<OpsNotificationService>.Instance);
    }
}
