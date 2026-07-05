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
        var eventStore = Substitute.For<IAlertEventStore>();
        var dispatchStore = Substitute.For<IAlertDispatchStore>();
        eventStore.TryAppendAsync(Arg.Any<AlertEventEnvelope>(), Arg.Any<CancellationToken>()).Returns(42L);

        var sut = Create(eventStore, dispatchStore, out _, enabled: true, channels: ["webhook"]);

        await sut.NotifyAsync(Notification(AlertSeverity.Critical), CancellationToken.None);

        // Ops event appended with the ops source discriminator.
        await eventStore.Received(1).TryAppendAsync(
            Arg.Is<AlertEventEnvelope>(e =>
                e.Source == AlertEventSources.Ops &&
                e.RuleId == 0 &&
                e.DedupeKey == "ops:deploy-workflow:op-1:Failed"),
            Arg.Any<CancellationToken>());

        // The outbox row the webhook sink will consume.
        await dispatchStore.Received(1).EnqueueAsync(
            42L,
            Arg.Is<ImmutableArray<AlertChannelType>>(c => c.Contains(AlertChannelType.Webhook)),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task NotifyAsync_WhenDisabled_DoesNotEnqueue()
    {
        var eventStore = Substitute.For<IAlertEventStore>();
        var dispatchStore = Substitute.For<IAlertDispatchStore>();
        var sut = Create(eventStore, dispatchStore, out _, enabled: false, channels: ["webhook"]);

        await sut.NotifyAsync(Notification(AlertSeverity.Critical), CancellationToken.None);

        await eventStore.DidNotReceive().TryAppendAsync(Arg.Any<AlertEventEnvelope>(), Arg.Any<CancellationToken>());
        await dispatchStore.DidNotReceive().EnqueueAsync(Arg.Any<long>(), Arg.Any<ImmutableArray<AlertChannelType>>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task NotifyAsync_BelowMinSeverity_DoesNotEnqueue()
    {
        var eventStore = Substitute.For<IAlertEventStore>();
        var dispatchStore = Substitute.For<IAlertDispatchStore>();
        var sut = Create(eventStore, dispatchStore, out _, enabled: true, channels: ["webhook"], minSeverity: AlertSeverity.Warning);

        await sut.NotifyAsync(Notification(AlertSeverity.Info), CancellationToken.None);

        await eventStore.DidNotReceive().TryAppendAsync(Arg.Any<AlertEventEnvelope>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task NotifyAsync_EditionGating_DropsDisallowedChannels()
    {
        var eventStore = Substitute.For<IAlertEventStore>();
        var dispatchStore = Substitute.For<IAlertDispatchStore>();
        eventStore.TryAppendAsync(Arg.Any<AlertEventEnvelope>(), Arg.Any<CancellationToken>()).Returns(7L);

        var sut = Create(
            eventStore,
            dispatchStore,
            out var editionPolicy,
            enabled: true,
            channels: ["webhook", "slack"]);

        // Pro-like edition: webhook allowed, slack (rich channel) not.
        editionPolicy.IsChannelAllowed(AlertChannelType.Webhook).Returns(true);
        editionPolicy.IsChannelAllowed(AlertChannelType.Slack).Returns(false);

        await sut.NotifyAsync(Notification(AlertSeverity.Critical), CancellationToken.None);

        await dispatchStore.Received(1).EnqueueAsync(
            7L,
            Arg.Is<ImmutableArray<AlertChannelType>>(c =>
                c.Contains(AlertChannelType.Webhook) && !c.Contains(AlertChannelType.Slack)),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task NotifyAsync_WhenEventDeduplicated_DoesNotEnqueueDispatch()
    {
        var eventStore = Substitute.For<IAlertEventStore>();
        var dispatchStore = Substitute.For<IAlertDispatchStore>();
        // Duplicate terminal event: the store reports the dedupe key already exists.
        eventStore.TryAppendAsync(Arg.Any<AlertEventEnvelope>(), Arg.Any<CancellationToken>()).Returns((long?)null);

        var sut = Create(eventStore, dispatchStore, out _, enabled: true, channels: ["webhook"]);

        await sut.NotifyAsync(Notification(AlertSeverity.Critical), CancellationToken.None);

        await eventStore.Received(1).TryAppendAsync(Arg.Any<AlertEventEnvelope>(), Arg.Any<CancellationToken>());
        await dispatchStore.DidNotReceive().EnqueueAsync(Arg.Any<long>(), Arg.Any<ImmutableArray<AlertChannelType>>(), Arg.Any<CancellationToken>());
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
        IAlertEventStore eventStore,
        IAlertDispatchStore dispatchStore,
        out IAlertEditionPolicy editionPolicy,
        bool enabled,
        IReadOnlyList<string> channels,
        AlertSeverity minSeverity = AlertSeverity.Info)
    {
        var writer = new AlertDispatchWriter(eventStore, dispatchStore, NullLogger<AlertDispatchWriter>.Instance);
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
        });

        return new OpsNotificationService(writer, editionPolicy, options, NullLogger<OpsNotificationService>.Instance);
    }
}
