// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;

namespace Honua.Alerts;

/// <summary>
/// Shared metadata builder for alert delivery message attributes.
/// </summary>
internal static class AlertDeliveryMetadata
{
    /// <summary>
    /// Builds the standard set of string message attributes for cloud providers (SNS, SQS).
    /// </summary>
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

    /// <summary>
    /// Builds extended metadata properties including incident status (for Event Hub, etc.).
    /// </summary>
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
