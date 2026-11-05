// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Alerts.Ops;

// Human-readable sinks render ops events from the payload, not the inert rule and layer scalars.
internal static class OpsNotificationPresentation
{
    public static OpsAlertPayload? TryResolve(AlertEventEnvelope alertEvent)
    {
        if (alertEvent is null ||
            !string.Equals(alertEvent.Source, AlertEventSources.Ops, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(
                alertEvent.PayloadJson,
                OpsNotificationJsonContext.Default.OpsAlertPayload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string FormatAttributes(
        IReadOnlyDictionary<string, string>? attributes,
        string separator)
    {
        if (attributes is null || attributes.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            separator,
            attributes
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"{pair.Key}: {pair.Value}"));
    }

    public static string SeverityEmoji(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => ":red_circle:",
        AlertSeverity.Warning => ":large_orange_circle:",
        _ => ":information_source:"
    };
}
