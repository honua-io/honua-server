// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;

namespace Honua.Postgres.Features.Alerts;

internal static class AlertStoreConversions
{
    public static AlertTriggerType ToTriggerType(short value)
    {
        return value switch
        {
            1 => AlertTriggerType.Enter,
            2 => AlertTriggerType.Exit,
            3 => AlertTriggerType.Dwell,
            4 => AlertTriggerType.Threshold,
            _ => throw new InvalidOperationException($"Unsupported alert trigger type '{value}'.")
        };
    }

    public static short ToDbValue(this AlertTriggerType value)
    {
        return value switch
        {
            AlertTriggerType.Enter => 1,
            AlertTriggerType.Exit => 2,
            AlertTriggerType.Dwell => 3,
            AlertTriggerType.Threshold => 4,
            _ => throw new InvalidOperationException($"Unsupported alert trigger type '{value}'.")
        };
    }

    public static AlertChangeOperation ToChangeOperation(short value)
    {
        return value switch
        {
            1 => AlertChangeOperation.Insert,
            2 => AlertChangeOperation.Update,
            3 => AlertChangeOperation.Delete,
            _ => throw new InvalidOperationException($"Unsupported alert change operation '{value}'.")
        };
    }

    public static AlertSeverity ToSeverity(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "info" => AlertSeverity.Info,
            "warning" => AlertSeverity.Warning,
            "critical" => AlertSeverity.Critical,
            _ => throw new InvalidOperationException($"Unsupported alert severity '{value}'.")
        };
    }

    public static string ToDbValue(this AlertSeverity value)
    {
        return value switch
        {
            AlertSeverity.Info => "info",
            AlertSeverity.Warning => "warning",
            AlertSeverity.Critical => "critical",
            _ => throw new InvalidOperationException($"Unsupported alert severity '{value}'.")
        };
    }

    public static AlertEdition ToEdition(short value)
    {
        return value switch
        {
            1 => AlertEdition.Pro,
            2 => AlertEdition.Enterprise,
            _ => throw new InvalidOperationException($"Unsupported alert edition '{value}'.")
        };
    }

    public static AlertDispatchStatus ToDispatchStatus(short value)
    {
        return value switch
        {
            0 => AlertDispatchStatus.Pending,
            1 => AlertDispatchStatus.Processing,
            2 => AlertDispatchStatus.Delivered,
            3 => AlertDispatchStatus.Failed,
            4 => AlertDispatchStatus.DeadLetter,
            _ => throw new InvalidOperationException($"Unsupported alert dispatch status '{value}'.")
        };
    }

    public static short ToDbValue(this AlertDispatchStatus value)
    {
        return value switch
        {
            AlertDispatchStatus.Pending => 0,
            AlertDispatchStatus.Processing => 1,
            AlertDispatchStatus.Delivered => 2,
            AlertDispatchStatus.Failed => 3,
            AlertDispatchStatus.DeadLetter => 4,
            _ => throw new InvalidOperationException($"Unsupported alert dispatch status '{value}'.")
        };
    }

    public static AlertIncidentStatus ToIncidentStatus(short value)
    {
        return value switch
        {
            1 => AlertIncidentStatus.Started,
            2 => AlertIncidentStatus.Ongoing,
            3 => AlertIncidentStatus.Ended,
            _ => throw new InvalidOperationException($"Unsupported alert incident status '{value}'.")
        };
    }

    public static short ToDbValue(this AlertIncidentStatus value)
    {
        return value switch
        {
            AlertIncidentStatus.Started => 1,
            AlertIncidentStatus.Ongoing => 2,
            AlertIncidentStatus.Ended => 3,
            _ => throw new InvalidOperationException($"Unsupported alert incident status '{value}'.")
        };
    }

    public static AlertChannelType ToChannelType(short value)
    {
        return value switch
        {
            1 => AlertChannelType.Webhook,
            2 => AlertChannelType.WebSocket,
            3 => AlertChannelType.Email,
            4 => AlertChannelType.Digest,
            5 => AlertChannelType.AwsSns,
            6 => AlertChannelType.AzureEventGrid,
            7 => AlertChannelType.Slack,
            8 => AlertChannelType.MicrosoftTeams,
            9 => AlertChannelType.AwsSqs,
            10 => AlertChannelType.AzureEventHub,
            _ => throw new InvalidOperationException($"Unsupported alert channel type '{value}'.")
        };
    }

    public static short ToDbValue(this AlertChannelType value)
    {
        return value switch
        {
            AlertChannelType.Webhook => 1,
            AlertChannelType.WebSocket => 2,
            AlertChannelType.Email => 3,
            AlertChannelType.Digest => 4,
            AlertChannelType.AwsSns => 5,
            AlertChannelType.AzureEventGrid => 6,
            AlertChannelType.Slack => 7,
            AlertChannelType.MicrosoftTeams => 8,
            AlertChannelType.AwsSqs => 9,
            AlertChannelType.AzureEventHub => 10,
            _ => throw new InvalidOperationException($"Unsupported alert channel type '{value}'.")
        };
    }

    public static AlertChannelType ParseChannel(string value)
    {
        return AlertChannelNames.Parse(value);
    }

    public static string ToChannelName(this AlertChannelType value)
    {
        return value.ToExternalName();
    }
}
