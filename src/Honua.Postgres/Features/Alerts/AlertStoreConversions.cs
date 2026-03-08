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

    public static AlertChannelType ToChannelType(short value)
    {
        return value switch
        {
            1 => AlertChannelType.Webhook,
            2 => AlertChannelType.WebSocket,
            3 => AlertChannelType.Email,
            4 => AlertChannelType.Digest,
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
            _ => throw new InvalidOperationException($"Unsupported alert channel type '{value}'.")
        };
    }

    public static AlertChannelType ParseChannel(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "webhook" => AlertChannelType.Webhook,
            "websocket" => AlertChannelType.WebSocket,
            "email" => AlertChannelType.Email,
            "digest" => AlertChannelType.Digest,
            _ => throw new InvalidOperationException($"Unsupported alert channel '{value}'.")
        };
    }

    public static string ToChannelName(this AlertChannelType value)
    {
        return value switch
        {
            AlertChannelType.Webhook => "webhook",
            AlertChannelType.WebSocket => "websocket",
            AlertChannelType.Email => "email",
            AlertChannelType.Digest => "digest",
            _ => throw new InvalidOperationException($"Unsupported alert channel type '{value}'.")
        };
    }
}
