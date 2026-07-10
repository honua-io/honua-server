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

namespace Honua.Server.Tests.Features.Alerts;

public sealed class OpsNotificationServiceTests
{
    [UnitTest]
    public async Task NotifyAsync_WhenEnabled_EnqueuesOpsEventToConfiguredChannel()
    {
        var outbox = Substitute.For<IAlertOutboxWriter>();
        outbox.AppendAndEnqueueAsync(Arg.Any<AlertEventEnvelope>(), Arg.Any<ImmutableArray<AlertChannelType>>(), Arg.Any<CancellationToken>())
            .Returns(42L);

        var sut = Create(outbox, out _, out _, enabled: true, channels: ["webhook"]);

        await sut.NotifyAsync(Notification(AlertSeverity.Critical), CancellationToken.None);

        // Ops event appended AND its dispatch enqueued atomically, with the ops source discriminator.
        await outbox.Received(1).AppendAndEnqueueAsync(
            Arg.Is<AlertEventEnvelope>(e =>
                e.Source == AlertEventSources.Ops &&
                e.RuleId == 0 &&
                e.DedupeKey == "ops:deploy-workflow:op-1:Failed"),
            Arg.Is<ImmutableArray<AlertChannelType>>(c => c.Contains(AlertChannelType.Webhook)),
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
        outbox.AppendAndEnqueueAsync(Arg.Any<AlertEventEnvelope>(), Arg.Any<ImmutableArray<AlertChannelType>>(), Arg.Any<CancellationToken>())
            .Returns(7L);

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

        await outbox.Received(1).AppendAndEnqueueAsync(
            Arg.Any<AlertEventEnvelope>(),
            Arg.Is<ImmutableArray<AlertChannelType>>(c =>
                c.Contains(AlertChannelType.Webhook) && !c.Contains(AlertChannelType.Slack)),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task NotifyAsync_WhenChannelCircuitOpen_PersistsEvidenceWithoutDispatch()
    {
        var outbox = Substitute.For<IAlertOutboxWriter>();
        outbox.AppendAndEnqueueAsync(Arg.Any<AlertEventEnvelope>(), Arg.Any<ImmutableArray<AlertChannelType>>(), Arg.Any<CancellationToken>())
            .Returns(9L);

        // Threshold 1 so a single dead-letter trips the breaker for the webhook channel.
        var sut = Create(outbox, out _, out var breaker, enabled: true, channels: ["webhook"], circuitThreshold: 1);
        breaker.RecordDeadLetter(AlertChannelType.Webhook, DateTimeOffset.UtcNow);

        await sut.NotifyAsync(Notification(AlertSeverity.Critical), CancellationToken.None);

        // The tripped channel gets no dispatch row (bounded dead-letter volume), but the ops event
        // remains durable so operators can still reconstruct what the autonomous system did.
        await outbox.Received(1).AppendAndEnqueueAsync(
            Arg.Is<AlertEventEnvelope>(alertEvent => alertEvent.Source == AlertEventSources.Ops),
            Arg.Is<ImmutableArray<AlertChannelType>>(channels => channels.IsEmpty),
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
        var writer = new AlertDispatchWriter(outbox, NullLogger<AlertDispatchWriter>.Instance);
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
        return new OpsNotificationService(writer, editionPolicy, breaker, options, NullLogger<OpsNotificationService>.Instance);
    }
}
