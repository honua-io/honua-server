// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Security.Admission;

namespace Honua.Alerts.Ops;

internal sealed class AiIncidentNoticePublisher(
    IAiIncidentNoticeStore notices,
    OpsNotificationService notifications)
{
    private const int BatchSize = 100;

    internal async Task<int> PublishPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await notices.ListPendingIncidentAsync(BatchSize, cancellationToken).ConfigureAwait(false);
        return await AiSpendNoticePublisher.PublishAsync(notifications,
            new AiSpendNoticePublisher.Batch<AiIncidentNotice>(pending, notices.MarkQueuedAsync, CreateNotification),
            cancellationToken).ConfigureAwait(false);
    }

    private static OpsNotification CreateNotification(AiIncidentNotice notice)
    {
        var episode = notice.EpisodeId.ToString("D");
        var identity = $"{notice.DeploymentId.Length}:{notice.DeploymentId}{notice.AuthorityScope.Length}:{notice.AuthorityScope}{episode}";
        return new OpsNotification
        {
            Source = "incident-spend",
            Severity = AlertSeverity.Warning,
            Title = "Incident spend reached 80%",
            Body = "An incident has reached 80% of a spend limit. Review its remaining allowance before further execution.",
            DedupeIdentifier = AiSpendNoticePublisher.DedupeIdentifier(identity),
            Attributes = new Dictionary<string, string>
            {
                ["deployment"] = notice.DeploymentId,
                ["authority"] = notice.AuthorityScope,
                ["episode"] = episode,
            },
        };
    }
}
