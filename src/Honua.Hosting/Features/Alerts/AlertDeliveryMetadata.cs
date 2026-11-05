// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;

namespace Honua.Alerts;

internal static class AlertDeliveryMetadata
{
    public static Dictionary<string, string> BuildStringAttributes(AlertEventEnvelope alertEvent)
    {
        return new Dictionary<string, string>
        {
            ["X-Honua-Alert-Rule"] = alertEvent.RuleId.ToString(),
            ["X-Honua-Alert-Event"] = alertEvent.DedupeKey,
            ["X-Honua-Trigger-Type"] = alertEvent.TriggerType.ToString(),
            ["X-Honua-Severity"] = alertEvent.Severity.ToString()
        };
    }

    public static Dictionary<string, object> BuildObjectProperties(AlertEventEnvelope alertEvent)
    {
        return new Dictionary<string, object>
        {
            ["X-Honua-Alert-Rule"] = alertEvent.RuleId.ToString(),
            ["X-Honua-Alert-Event"] = alertEvent.DedupeKey,
            ["X-Honua-Trigger-Type"] = alertEvent.TriggerType.ToString(),
            ["X-Honua-Severity"] = alertEvent.Severity.ToString(),
            ["X-Honua-Incident-Status"] = alertEvent.IncidentStatus.ToString()
        };
    }
}
