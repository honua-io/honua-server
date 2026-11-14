// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Alerts.Ops;
using Honua.ControlPlane;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

internal sealed class OpsNotificationReminderRunner(
    PostgresOpsNotificationStore schedules,
    IOpsFindingsService findingsService,
    IOperationProposalStore proposals,
    OpsNotificationService notifications,
    TimeProvider timeProvider)
{
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        await schedules.ExpireApprovalsAsync(100, cancellationToken).ConfigureAwait(false);
        var findings = await findingsService.EvaluateAsync(cancellationToken).ConfigureAwait(false);
        var actionable = findings.Where(finding => finding.Severity >= OpsFindingSeverity.Warning)
            .ToDictionary(finding => finding.Id, StringComparer.Ordinal);
        var now = timeProvider.GetUtcNow();
        foreach (var finding in actionable.Values)
        {
            await schedules.ObserveAsync("finding", finding.Id, finding.Severity, now, cancellationToken)
                .ConfigureAwait(false);
        }
        await schedules.CloseMissingAsync("finding", actionable.Keys.ToArray(), now, cancellationToken)
            .ConfigureAwait(false);
        await schedules.ObserveAwaitingAsync(100, cancellationToken).ConfigureAwait(false);
        var due = await schedules.ListDueAsync(now, 100, cancellationToken).ConfigureAwait(false);
        foreach (var schedule in due)
        {
            await DispatchAsync(schedule, actionable, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(OpsNotificationSchedule schedule,
        Dictionary<string, OpsFinding> findings, CancellationToken cancellationToken)
    {
        var terminal = false;
        OperationProposal? proposal = null;
        if (schedule.Source == "proposal")
        {
            proposal = await proposals.GetAsync(schedule.SubjectId, cancellationToken).ConfigureAwait(false);
            terminal = proposal?.Status == OperationProposalStatus.Expired;
            if (!terminal && proposal?.Status != OperationProposalStatus.AwaitingApproval)
            {
                await schedules.TryCloseAsync(schedule, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        else if (!findings.ContainsKey(schedule.SubjectId))
        {
            return;
        }

        var notification = new OpsNotification
        {
            Source = "ops-reminders",
            Severity = schedule.Severity == OpsFindingSeverity.Critical ? AlertSeverity.Critical : AlertSeverity.Warning,
            Title = terminal ? "Approval expired without execution" :
                schedule.Source == "proposal" ? "Human approval required" : "Operational finding remains open",
            Body = $"{schedule.Source} {schedule.SubjectId}. Review in the authenticated operations console at /operate/copilot.",
            DedupeIdentifier = $"{schedule.Source}:{schedule.SubjectId}:{schedule.EpisodeId:N}:{schedule.Severity}:{schedule.NoticeNumber}",
            ProposalId = proposal?.ProposalId,
            OperationInstanceId = proposal?.Audit?.OperationInstanceId,
            CorrelationId = proposal?.Audit?.CorrelationId,
            AuditId = proposal?.Audit?.AuditId,
        };
        if (!await notifications.TryNotifyAsync(notification, cancellationToken).ConfigureAwait(false))
        {
            // Keep the pending ordinal, but let later due rows progress when this row is filtered
            // or delivery is disabled. A later enable/retry still uses the same outbox identity.
            await schedules.TryDeferAsync(schedule, timeProvider.GetUtcNow().AddMinutes(1), cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        var now = timeProvider.GetUtcNow();
        if (terminal)
        {
            await schedules.TryCloseAsync(schedule, now, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var next = schedule.Source == "proposal"
                ? now.AddDays(1)
                : OpsNotificationCadence.NextNotice(schedule.Severity, schedule.FirstSeenAt, now);
            await schedules.TryAdvanceAsync(schedule, next, cancellationToken).ConfigureAwait(false);
        }
    }
}
