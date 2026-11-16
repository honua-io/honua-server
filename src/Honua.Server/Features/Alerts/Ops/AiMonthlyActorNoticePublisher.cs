// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
        var queued = 0;
        foreach (var notice in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await notifications.TryNotifyAsync(CreateNotification(notice), cancellationToken).ConfigureAwait(false)
                && await notices.MarkQueuedAsync(notice, cancellationToken).ConfigureAwait(false))
            {
                queued++;
            }
        }

        return queued;
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
            DedupeIdentifier = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))),
            Attributes = new Dictionary<string, string>
            {
                ["deployment"] = notice.DeploymentId,
                ["actor"] = notice.ActorKey,
                ["month"] = month,
            },
        };
    }
}
