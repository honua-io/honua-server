// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Alerts.Ops;

/// <summary>
/// Resolves the human-readable presentation of an ops notification carried on a
/// shared alert-delivery envelope. Operations notifications (deploy/job terminal
/// events) reuse the GIS alert outbox but persist their meaningful title/body in
/// <see cref="AlertEventEnvelope.PayloadJson"/> while the rule/zone/layer/object/
/// trigger scalars are inert placeholders (#2427). Human-readable sinks (Slack,
/// Teams, Email) must render ops events from this payload rather than the GIS
/// scalars, which would otherwise post meaningless "Rule: 0, Layer: 0" text.
/// </summary>
internal static class OpsNotificationPresentation
{
    /// <summary>
    /// Returns the parsed ops payload when <paramref name="alertEvent"/> is an
    /// operations notification (<see cref="AlertEventSources.Ops"/>); otherwise
    /// <see langword="null"/> so callers fall back to GIS formatting.
    /// </summary>
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

    /// <summary>
    /// Formats ops attributes (operationId, workload, status, phase, …) as a stable,
    /// key-ordered string joined by <paramref name="separator"/>. Empty when there
    /// are no attributes.
    /// </summary>
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

    /// <summary>
    /// Severity-derived emoji used by human-readable sinks. Ops events do not carry a
    /// meaningful incident status, so presentation keys off severity instead.
    /// </summary>
    public static string SeverityEmoji(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => ":red_circle:",
        AlertSeverity.Warning => ":large_orange_circle:",
        _ => ":information_source:"
    };
}
