// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Alerts.Ops;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Security.Admission;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

public sealed partial class OpsNotificationServiceTests
{
    [UnitTest]
    public async Task PublishPendingAsync_Enabled_AcknowledgesOnlyAfterDurableOutboxCommit()
    {
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var notifications = Create(outbox, out _, out _, enabled: true, channels: ["webhook"]);
        var store = PendingMonthlyNotice();
        store.MarkQueuedAsync(Arg.Any<AiMonthlyActorNotice>(), Arg.Any<CancellationToken>()).Returns(true);
        var publisher = new AiMonthlyActorNoticePublisher(store, notifications);

        (await publisher.PublishPendingAsync(default)).Should().Be(1);

        Received.InOrder(() =>
        {
            outbox.CommitEvaluationAsync(Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
                Arg.Is<IReadOnlyList<AlertOutboxEntry>>(entries => entries.Count == 1 &&
                    entries[0].AlertEvent.DedupeKey.Contains("monthly-actor-spend") &&
                    entries[0].AlertEvent.Severity == AlertSeverity.Warning), Arg.Any<CancellationToken>());
            store.MarkQueuedAsync(Arg.Any<AiMonthlyActorNotice>(), Arg.Any<CancellationToken>());
        });
    }

    [UnitTest]
    public async Task PublishPendingAsync_Disabled_LeavesNoticePendingForRestart()
    {
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var notifications = Create(outbox, out _, out _, enabled: false, channels: ["webhook"]);
        var store = PendingMonthlyNotice();

        (await new AiMonthlyActorNoticePublisher(store, notifications).PublishPendingAsync(default)).Should().Be(0);

        await store.DidNotReceive().MarkQueuedAsync(Arg.Any<AiMonthlyActorNotice>(), Arg.Any<CancellationToken>());
        await outbox.DidNotReceive().CommitEvaluationAsync(Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
            Arg.Any<IReadOnlyList<AlertOutboxEntry>>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task PublishPendingAsync_OutboxFailure_PropagatesAndLeavesNoticePending()
    {
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var notifications = Create(outbox, out _, out _, enabled: true, channels: ["webhook"]);
        outbox.CommitEvaluationAsync(Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
                Arg.Any<IReadOnlyList<AlertOutboxEntry>>(), Arg.Any<CancellationToken>())
            .Returns<ImmutableArray<bool>>(_ => throw new InvalidOperationException("outbox unavailable"));
        var store = PendingMonthlyNotice();
        var publisher = new AiMonthlyActorNoticePublisher(store, notifications);

        await publisher.Invoking(sut => sut.PublishPendingAsync(default)).Should().ThrowAsync<InvalidOperationException>();
        await store.DidNotReceive().MarkQueuedAsync(Arg.Any<AiMonthlyActorNotice>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task PublishPendingAsync_AcknowledgementFailure_RetryUsesSameDurableDedupeKey()
    {
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var notifications = Create(outbox, out _, out _, enabled: true, channels: ["webhook"]);
        var keys = new List<string>();
        outbox.CommitEvaluationAsync(Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
                Arg.Any<IReadOnlyList<AlertOutboxEntry>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                keys.Add(call.ArgAt<IReadOnlyList<AlertOutboxEntry>>(1).Single().AlertEvent.DedupeKey);
                return ImmutableArray.Create(keys.Count == 1);
            });
        var store = PendingMonthlyNotice();
        store.MarkQueuedAsync(Arg.Any<AiMonthlyActorNotice>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("acknowledgement unavailable"), _ => true);

        await new AiMonthlyActorNoticePublisher(store, notifications).Invoking(sut => sut.PublishPendingAsync(default))
            .Should().ThrowAsync<InvalidOperationException>();
        (await new AiMonthlyActorNoticePublisher(store, notifications).PublishPendingAsync(default)).Should().Be(1);
        keys.Should().HaveCount(2).And.OnlyContain(key => key == keys[0]);
    }

    private static IAiMonthlyActorNoticeStore PendingMonthlyNotice()
    {
        var store = Substitute.For<IAiMonthlyActorNoticeStore>();
        store.ListPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new[]
        {
            new AiMonthlyActorNotice
            {
                DeploymentId = "deployment", ActorKey = new string('a', 64),
                MonthStart = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
                ChargedTokens = 24, ChargedMoney = 8, TokenLimit = 30, MoneyLimit = 10,
            },
        });
        return store;
    }
}
