// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Maps alert-event summaries onto the Console Operate wire shape.
/// </summary>
internal static class ObservabilityAlertEventResponseMapper
{
    public static ObservabilityAlertEventResponse Map(AlertEventSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new ObservabilityAlertEventResponse
        {
            EventId = summary.EventId,
            RuleId = summary.RuleId,
            RuleName = summary.RuleName,
            ZoneId = summary.ZoneId,
            ServiceId = summary.ServiceId,
            LayerId = summary.LayerId,
            ObjectId = summary.ObjectId,
            TriggerType = summary.TriggerType.ToString().ToLowerInvariant(),
            Severity = summary.Severity.ToString().ToLowerInvariant(),
            OccurredAt = summary.OccurredAt,
            IncidentStatus = summary.IncidentStatus.ToString().ToLowerInvariant(),
            IncidentDurationMs = summary.IncidentDurationMs,
            LifecycleStatus = summary.LifecycleStatus.ToString().ToLowerInvariant(),
            AcknowledgedAt = summary.AcknowledgedAt,
            AcknowledgedBy = summary.AcknowledgedBy,
            SuppressedUntil = summary.SuppressedUntil,
            ResolvedAt = summary.ResolvedAt,
            ResolvedBy = summary.ResolvedBy,
            ResourceRef = "alert/" + summary.EventId.ToString(CultureInfo.InvariantCulture)
        };
    }
}
