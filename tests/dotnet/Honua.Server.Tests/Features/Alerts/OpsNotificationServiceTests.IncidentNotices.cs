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
    public async Task IncidentNotice_RetryRetainsIdentity_NewEpisodeHasIndependentIdentity()
    {
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var notifications = Create(outbox, out _, out _, enabled: true, channels: ["webhook"]);
        var entries = new List<AlertOutboxEntry>();
        outbox.CommitEvaluationAsync(Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
                Arg.Any<IReadOnlyList<AlertOutboxEntry>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                entries.Add(call.ArgAt<IReadOnlyList<AlertOutboxEntry>>(1).Single());
                return ImmutableArray.Create(true);
            });
        var store = Substitute.For<IAiIncidentNoticeStore>();
        var notice = new AiIncidentNotice
        {
            DeploymentId = "deployment",
            AuthorityScope = "ops:deployment",
            EpisodeId = Guid.NewGuid(),
            ChargedTokens = 24,
            ChargedMoney = 8,
            TokenLimit = 30,
            MoneyLimit = 10,
        };
        store.ListPendingIncidentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { notice }, new[] { notice }, new[] { notice with { EpisodeId = Guid.NewGuid() } });
        store.MarkQueuedAsync(Arg.Any<AiIncidentNotice>(), Arg.Any<CancellationToken>()).Returns(true);
        for (var index = 0; index < 3; index++)
            (await new AiIncidentNoticePublisher(store, notifications).PublishPendingAsync(default)).Should().Be(1);

        entries.Should().HaveCount(3);
        entries[0].AlertEvent.DedupeKey.Should().Contain("incident-spend").And.Be(entries[1].AlertEvent.DedupeKey);
        entries[2].AlertEvent.DedupeKey.Should().NotBe(entries[0].AlertEvent.DedupeKey);
        entries[0].AlertEvent.Severity.Should().Be(AlertSeverity.Warning);
        entries[0].AlertEvent.PayloadJson.Should().Contain(notice.EpisodeId.ToString("D")).And.Contain("ops:deployment");
    }
}
