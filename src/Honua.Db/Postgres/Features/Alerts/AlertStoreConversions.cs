// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Alerts.Domain;
using Npgsql;

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

    /// <summary>
    /// Maps a data reader row to an <see cref="AlertRuleDefinition"/>.
    /// Expects columns: rule_id, service_id, layer_id, zone_id, rule_name, trigger_type,
    /// conditions, cooldown_seconds, severity, edition_required, channels, is_active.
    /// </summary>
    internal static AlertRuleDefinition MapRule(NpgsqlDataReader reader)
    {
        return new AlertRuleDefinition
        {
            RuleId = reader.GetInt64(0),
            ServiceId = reader.GetString(1),
            LayerId = reader.GetInt32(2),
            ZoneId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
            RuleName = reader.GetString(4),
            TriggerType = ToTriggerType(reader.GetInt16(5)),
            ConditionsJson = reader.IsDBNull(6) ? "{}" : reader.GetString(6),
            CooldownSeconds = reader.GetInt32(7),
            Severity = ToSeverity(reader.GetString(8)),
            EditionRequired = ToEdition(reader.GetInt16(9)),
            Channels = ParseChannels(reader.IsDBNull(10) ? [] : (string[])reader[10]),
            IsActive = reader.GetBoolean(11)
        };
    }

    /// <summary>
    /// Parses an array of channel name strings into an immutable array of <see cref="AlertChannelType"/>.
    /// Skips unrecognized values to remain resilient to data from previous versions.
    /// </summary>
    internal static ImmutableArray<AlertChannelType> ParseChannels(string[] values)
    {
        if (values.Length == 0)
        {
            return ImmutableArray<AlertChannelType>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<AlertChannelType>(values.Length);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            try
            {
                builder.Add(ParseChannel(value));
            }
            catch (InvalidOperationException)
            {
                // Skip unsupported channel values persisted by previous versions.
            }
        }

        return builder.Distinct().ToImmutableArray();
    }

    /// <summary>
    /// Maps a data reader row to an <see cref="AlertZoneDefinition"/>.
    /// Expects columns: zone_id, service_id, zone_name, ST_AsBinary(geometry), ST_SRID(geometry), metadata, is_active.
    /// </summary>
    internal static AlertZoneDefinition MapZone(NpgsqlDataReader reader)
    {
        return new AlertZoneDefinition
        {
            ZoneId = reader.GetInt64(0),
            ServiceId = reader.GetString(1),
            ZoneName = reader.GetString(2),
            Geometry = reader.IsDBNull(3) ? null : (byte[])reader[3],
            GeometrySrid = reader.IsDBNull(4) ? null : reader.GetInt32(4),
            Metadata = AlertMetadataSerializer.Parse(reader.IsDBNull(5) ? "{}" : reader.GetString(5)),
            IsActive = reader.GetBoolean(6)
        };
    }
}
