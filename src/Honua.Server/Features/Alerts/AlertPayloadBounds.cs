// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Alerts;

/// <summary>
/// Keeps a delivered alert payload inside the selected channel's publish limit.
/// Oversized geometry is replaced with a reference. A payload that still does not fit
/// is replaced with an explicit rejection notice and is still enqueued.
/// </summary>
internal static class AlertPayloadBounds
{
    /// <summary>SNS Publish rejects messages larger than 256 KiB.</summary>
    internal const int SnsLimitBytes = 256 * 1024;

    /// <summary>SQS message body limit.</summary>
    internal const int SqsLimitBytes = 256 * 1024;

    /// <summary>Ceiling for channels whose providers accept about 1 MiB.</summary>
    internal const int DefaultLimitBytes = 1024 * 1024;

    internal static AlertEventEnvelope Fit(AlertEventEnvelope alertEvent, ImmutableArray<AlertChannelType> channels)
    {
        ArgumentNullException.ThrowIfNull(alertEvent);
        if (channels.IsDefaultOrEmpty)
        {
            return alertEvent;
        }

        var limit = LimitBytes(channels);
        var payload = alertEvent.PayloadJson ?? "{}";
        if (Encoding.UTF8.GetByteCount(payload) <= limit)
        {
            return alertEvent;
        }

        JsonObject node;
        try
        {
            node = JsonNode.Parse(payload) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            node = new JsonObject();
        }

        var geometryHex = node["geometryWkbHex"] is JsonValue geometryValue && geometryValue.TryGetValue<string>(out var hex)
            ? hex
            : null;
        node.Remove("geometryWkbHex");
        node["geometryOmitted"] = true;
        node["geometryByteLength"] = geometryHex is null ? 0 : geometryHex.Length / 2;
        node["geometryRef"] = string.Create(
            CultureInfo.InvariantCulture,
            $"feature:{alertEvent.LayerId}/{alertEvent.ObjectId}@{alertEvent.Generation}");

        payload = node.ToJsonString();
        if (Encoding.UTF8.GetByteCount(payload) <= limit)
        {
            return alertEvent with { PayloadJson = payload };
        }

        var attributeJson = node["attributes"]?.ToJsonString();
        node.Remove("attributes");
        node["attributesOmitted"] = true;
        node["attributeByteLength"] = attributeJson is null ? 0 : Encoding.UTF8.GetByteCount(attributeJson);
        payload = node.ToJsonString();
        if (Encoding.UTF8.GetByteCount(payload) <= limit)
        {
            return alertEvent with { PayloadJson = payload };
        }

        var rejected = new JsonObject
        {
            ["payloadRejected"] = true,
            ["reason"] = "payload exceeds the selected channel limit",
            ["limitBytes"] = limit,
            ["serviceId"] = alertEvent.ServiceId,
            ["layerId"] = alertEvent.LayerId,
            ["objectId"] = alertEvent.ObjectId,
            ["ruleId"] = alertEvent.RuleId,
            ["generation"] = alertEvent.Generation,
            ["trigger"] = alertEvent.TriggerType.ToString()
        };
        return alertEvent with { PayloadJson = rejected.ToJsonString() };
    }

    internal static int LimitBytes(ImmutableArray<AlertChannelType> channels)
    {
        return channels.IsDefaultOrEmpty
            ? int.MaxValue
            : channels.Select(LimitBytes).Min();
    }

    internal static int LimitBytes(AlertChannelType channel)
        => channel switch
        {
            AlertChannelType.AwsSns => SnsLimitBytes,
            AlertChannelType.AwsSqs => SqsLimitBytes,
            AlertChannelType.Slack => SqsLimitBytes,
            AlertChannelType.MicrosoftTeams => SqsLimitBytes,
            _ => DefaultLimitBytes
        };
}
