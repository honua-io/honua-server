// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Security.Admission;

namespace Honua.Alerts.Ops;

internal sealed class AiMonthlyActorNoticePublisher(
    IAiMonthlyActorNoticeStore notices,
    OpsNotificationService notifications)
{
    private const int BatchSize = 100;

    internal async Task<int> PublishPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await notices.ListPendingAsync(BatchSize, cancellationToken).ConfigureAwait(false);
        return await AiSpendNoticePublisher.PublishAsync(notifications,
            new AiSpendNoticePublisher.Batch<AiMonthlyActorNotice>(pending, notices.MarkQueuedAsync, CreateNotification),
            cancellationToken).ConfigureAwait(false);
    }

    private static OpsNotification CreateNotification(AiMonthlyActorNotice notice)
    {
        var month = notice.MonthStart.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var identity = $"{notice.DeploymentId.Length}:{notice.DeploymentId}{notice.ActorKey.Length}:{notice.ActorKey}{month}";
        return new OpsNotification
        {
            Source = "monthly-actor-spend",
            Severity = AlertSeverity.Warning,
            Title = "Monthly agent spend reached 80%",
            Body = "An agent has reached 80% of a monthly spend limit. Review its remaining allowance before further execution.",
            DedupeIdentifier = AiSpendNoticePublisher.DedupeIdentifier(identity),
            Attributes = new Dictionary<string, string>
            {
                ["deployment"] = notice.DeploymentId,
                ["actor"] = notice.ActorKey,
                ["month"] = month,
            },
        };
    }
}
