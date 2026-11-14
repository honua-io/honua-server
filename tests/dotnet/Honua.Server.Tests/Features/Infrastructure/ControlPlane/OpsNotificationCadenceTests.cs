// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class OpsNotificationCadenceTests
{
    [Theory]
    [InlineData(0, 30)]
    [InlineData(30, 120)]
    [InlineData(120, 840)]
    [InlineData(840, 1560)]
    [InlineData(6000, 6720)]
    public void Critical_FollowsEscalationCadenceWithoutReplayingMissedReminders(int elapsedMinutes, int nextMinutes)
    {
        var firstSeen = new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
        var next = OpsNotificationCadence.NextNotice(OpsFindingSeverity.Critical, firstSeen,
            firstSeen.AddMinutes(elapsedMinutes));
        Assert.Equal(firstSeen.AddMinutes(nextMinutes), next);
    }

    [Fact]
    public void Warning_UsesDailyReminderFromSuccessfulEnqueue()
    {
        var firstSeen = new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
        var now = firstSeen.AddHours(3);
        Assert.Equal(now.AddHours(24),
            OpsNotificationCadence.NextNotice(OpsFindingSeverity.Warning, firstSeen, now));
    }
}
