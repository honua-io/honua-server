// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;

namespace Honua.Server.Tests.Features.Alerts;

internal static partial class AlertTestFixtures
{
    public static AlertDispatchItem CreateDispatchItem(
        AlertChannelType channelType,
        string? destination = null) => new()
        {
            DispatchId = 1,
            EventId = 2,
            ClaimToken = Guid.NewGuid(),
            ChannelType = channelType,
            Destination = destination,
            Status = AlertDispatchStatus.Pending,
            Attempts = 0,
            MaxAttempts = 3,
            NextAttemptAt = DateTimeOffset.UtcNow
        };

    public static AlertEventEnvelope CreateAlertEvent(
        string dedupeKey = "evt-test-1",
        AlertSeverity severity = AlertSeverity.Warning,
        AlertIncidentStatus incidentStatus = AlertIncidentStatus.Started) => new()
        {
            DedupeKey = dedupeKey,
            RuleId = 42,
            ServiceId = "svc-1",
            LayerId = 7,
            ObjectId = 100,
            TriggerType = AlertTriggerType.Enter,
            Generation = 1,
            Severity = severity,
            OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = "{\"test\":true}",
            IncidentStatus = incidentStatus
        };
}
