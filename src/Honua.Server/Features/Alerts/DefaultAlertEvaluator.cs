// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using NetTopologySuite.IO;

namespace Honua.Alerts;

internal sealed class DefaultAlertEvaluator : IAlertEvaluator
{
    private static readonly WKBReader _wkbReader = new();

    public Task<AlertEvaluationResult> EvaluateAsync(
        AlertChange change,
        Feature? feature,
        AlertRuleDefinition rule,
        AlertZoneDefinition? zone,
        AlertStateSnapshot? currentState,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var previousInside = currentState?.Inside ?? false;
        var insideNow = DetermineInside(change, feature, zone, previousInside);
        var enteredAt = ResolveEnteredAt(previousInside, insideNow, currentState?.EnteredAt, evaluatedAt);
        var lastAlertAt = currentState?.LastAlertAt;
        var thresholdStateJson = currentState?.ThresholdStateJson ?? "{}";

        AlertEventEnvelope? generatedEvent = null;

        switch (rule.TriggerType)
        {
            case AlertTriggerType.Enter:
                if (!previousInside && insideNow && CanEmit(rule.CooldownSeconds, lastAlertAt, evaluatedAt))
                {
                    generatedEvent = CreateEvent(change, rule, evaluatedAt, "enter", feature, zone,
                        evaluatedAt.ToUnixTimeSeconds(), AlertIncidentStatus.Started, 0);
                }
                break;

            case AlertTriggerType.Exit:
                if (previousInside && !insideNow && CanEmit(rule.CooldownSeconds, lastAlertAt, evaluatedAt))
                {
                    var durationMs = (long)(evaluatedAt - (currentState?.EnteredAt ?? evaluatedAt)).TotalMilliseconds;
                    generatedEvent = CreateEvent(change, rule, evaluatedAt, "exit", feature, zone,
                        evaluatedAt.ToUnixTimeSeconds(), AlertIncidentStatus.Ended, durationMs);
                }
                break;

            case AlertTriggerType.Dwell:
                if (insideNow)
                {
                    var dwellSeconds = TryGetIntCondition(rule.ConditionsJson, "dwellSeconds", 0);
                    var elapsedSeconds = (evaluatedAt - (enteredAt ?? evaluatedAt)).TotalSeconds;
                    if (elapsedSeconds >= dwellSeconds && CanEmit(rule.CooldownSeconds, lastAlertAt, evaluatedAt))
                    {
                        var durationMs = (long)(evaluatedAt - (enteredAt ?? evaluatedAt)).TotalMilliseconds;
                        var status = lastAlertAt.HasValue ? AlertIncidentStatus.Ongoing : AlertIncidentStatus.Started;
                        generatedEvent = CreateEvent(change, rule, evaluatedAt, "dwell", feature, zone,
                            evaluatedAt.ToUnixTimeSeconds(), status, durationMs);
                    }
                }
                break;

            case AlertTriggerType.Threshold:
                {
                    var (previousBreached, previousBreachedSince) = ParseThresholdState(currentState?.ThresholdStateJson);
                    var breachedNow = EvaluateThreshold(rule.ConditionsJson, feature);

                    // Track the breach-start timestamp separately from the last-emitted
                    // timestamp: if cooldown suppresses the "threshold" start event the
                    // resolve event still needs the correct incident duration.
                    DateTimeOffset? breachedSince = breachedNow
                        ? (previousBreached ? previousBreachedSince ?? evaluatedAt : evaluatedAt)
                        : null;
                    thresholdStateJson = CreateThresholdStateJson(breachedNow, breachedSince);

                    if (!previousBreached && breachedNow && CanEmit(rule.CooldownSeconds, lastAlertAt, evaluatedAt))
                    {
                        generatedEvent = CreateEvent(change, rule, evaluatedAt, "threshold", feature, zone,
                            evaluatedAt.ToUnixTimeSeconds(), AlertIncidentStatus.Started, 0);
                    }
                    else if (previousBreached && !breachedNow && CanEmit(rule.CooldownSeconds, lastAlertAt, evaluatedAt))
                    {
                        var durationMs = (long)(evaluatedAt - (previousBreachedSince ?? evaluatedAt)).TotalMilliseconds;
                        generatedEvent = CreateEvent(change, rule, evaluatedAt, "threshold_resolved", feature, zone,
                            evaluatedAt.ToUnixTimeSeconds(), AlertIncidentStatus.Ended, durationMs);
                    }

                    break;
                }

            default:
                break;
        }

        if (generatedEvent is not null)
        {
            lastAlertAt = evaluatedAt;
        }

        var updatedState = new AlertStateSnapshot
        {
            RuleId = rule.RuleId,
            LayerId = change.LayerId,
            ObjectId = change.ObjectId,
            Inside = insideNow,
            EnteredAt = enteredAt,
            LastAlertAt = lastAlertAt,
            LastGeneration = change.Generation,
            ThresholdStateJson = thresholdStateJson
        };

        var events = generatedEvent is null
            ? ImmutableArray<AlertEventEnvelope>.Empty
            : ImmutableArray.Create(generatedEvent);

        return Task.FromResult(new AlertEvaluationResult
        {
            UpdatedState = updatedState,
            Events = events
        });
    }

    private static AlertEventEnvelope CreateEvent(
        AlertChange change,
        AlertRuleDefinition rule,
        DateTimeOffset occurredAt,
        string transition,
        Feature? feature,
        AlertZoneDefinition? zone,
        long occurrenceToken,
        AlertIncidentStatus incidentStatus,
        long incidentDurationMs)
    {
        var dedupeKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{rule.RuleId}:{change.ObjectId}:{rule.TriggerType}:{change.Generation}:{occurrenceToken}");

        var payload = new JsonObject
        {
            ["serviceId"] = rule.ServiceId,
            ["layerId"] = change.LayerId,
            ["objectId"] = change.ObjectId,
            ["ruleId"] = rule.RuleId,
            ["ruleName"] = rule.RuleName,
            ["trigger"] = rule.TriggerType.ToString(),
            ["transition"] = transition,
            ["incidentStatus"] = incidentStatus.ToString(),
            ["incidentDurationMs"] = incidentDurationMs,
            ["generation"] = change.Generation,
            ["occurredAt"] = occurredAt,
            ["zoneId"] = zone?.ZoneId,
            ["zoneName"] = zone?.ZoneName,
            ["featurePresent"] = feature.HasValue
        };

        return new AlertEventEnvelope
        {
            DedupeKey = dedupeKey,
            RuleId = rule.RuleId,
            ZoneId = rule.ZoneId,
            ServiceId = rule.ServiceId,
            LayerId = change.LayerId,
            ObjectId = change.ObjectId,
            TriggerType = rule.TriggerType,
            Generation = change.Generation,
            Severity = rule.Severity,
            OccurredAt = occurredAt,
            PayloadJson = payload.ToJsonString(),
            IncidentStatus = incidentStatus,
            IncidentDurationMs = incidentDurationMs
        };
    }

    private static bool DetermineInside(
        AlertChange change,
        Feature? feature,
        AlertZoneDefinition? zone,
        bool previousInside)
    {
        if (change.Operation == AlertChangeOperation.Delete)
        {
            return false;
        }

        if (zone is null || !zone.IsActive)
        {
            return previousInside;
        }

        if (!feature.HasValue)
        {
            return false;
        }

        var featureValue = feature.Value;
        if (zone.Geometry is null || featureValue.Geometry is null)
        {
            return false;
        }

        try
        {
            var zoneGeometry = _wkbReader.Read(zone.Geometry);
            if (zone.GeometrySrid.HasValue && zoneGeometry.SRID <= 0)
            {
                zoneGeometry.SRID = zone.GeometrySrid.Value;
            }

            var featureGeometry = _wkbReader.Read(featureValue.Geometry);

            // Bail out when SRIDs are known but differ — planar comparison
            // across different projections produces incorrect results.
            if (zoneGeometry.SRID > 0 && featureGeometry.SRID > 0 &&
                zoneGeometry.SRID != featureGeometry.SRID)
            {
                return previousInside;
            }

            return zoneGeometry.Intersects(featureGeometry);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (ParseException)
        {
            return false;
        }
    }

    private static DateTimeOffset? ResolveEnteredAt(
        bool previousInside,
        bool insideNow,
        DateTimeOffset? existingEnteredAt,
        DateTimeOffset evaluatedAt)
    {
        if (!insideNow)
        {
            return null;
        }

        if (!previousInside)
        {
            return evaluatedAt;
        }

        return existingEnteredAt ?? evaluatedAt;
    }

    private static bool CanEmit(int cooldownSeconds, DateTimeOffset? lastAlertAt, DateTimeOffset evaluatedAt)
    {
        if (cooldownSeconds <= 0 || !lastAlertAt.HasValue)
        {
            return true;
        }

        return (evaluatedAt - lastAlertAt.Value).TotalSeconds >= cooldownSeconds;
    }

    private static int TryGetIntCondition(string json, string propertyName, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return defaultValue;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(propertyName, out var property))
            {
                return defaultValue;
            }

            return property.ValueKind switch
            {
                JsonValueKind.Number when property.TryGetInt32(out var intValue) => intValue,
                JsonValueKind.String when int.TryParse(property.GetString(), CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => defaultValue
            };
        }
        catch (JsonException)
        {
            return defaultValue;
        }
    }

    private static (bool Breached, DateTimeOffset? BreachedSince) ParseThresholdState(string? stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson))
        {
            return (false, null);
        }

        try
        {
            using var document = JsonDocument.Parse(stateJson);
            var root = document.RootElement;
            var breached = root.TryGetProperty("breached", out var breachedProp)
                && breachedProp.ValueKind == JsonValueKind.True;

            DateTimeOffset? breachedSince = null;
            if (root.TryGetProperty("breachedSince", out var sinceProp)
                && sinceProp.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    sinceProp.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                breachedSince = parsed;
            }

            return (breached, breachedSince);
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }

    private static string CreateThresholdStateJson(bool breached, DateTimeOffset? breachedSince)
    {
        var state = new JsonObject
        {
            ["breached"] = breached
        };

        if (breached && breachedSince.HasValue)
        {
            state["breachedSince"] = breachedSince.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }

        return state.ToJsonString();
    }

    private static bool EvaluateThreshold(string conditionsJson, Feature? feature)
    {
        if (!feature.HasValue || feature.Value.Attributes.Count == 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(conditionsJson);
            if (!document.RootElement.TryGetProperty("field", out var fieldProperty) ||
                !document.RootElement.TryGetProperty("operator", out var operatorProperty) ||
                !document.RootElement.TryGetProperty("value", out var valueProperty))
            {
                return false;
            }

            var fieldName = fieldProperty.GetString();
            var comparisonOperator = operatorProperty.GetString();

            if (string.IsNullOrWhiteSpace(fieldName) || string.IsNullOrWhiteSpace(comparisonOperator))
            {
                return false;
            }

            if (!TryGetAttributeValue(feature.Value, fieldName, out var attributeValue) ||
                !TryConvertToDouble(attributeValue, out var left) ||
                !TryConvertToDouble(valueProperty, out var right))
            {
                return false;
            }

            return comparisonOperator switch
            {
                ">" => left > right,
                ">=" => left >= right,
                "<" => left < right,
                "<=" => left <= right,
                "==" => Math.Abs(left - right) < double.Epsilon,
                "!=" => Math.Abs(left - right) >= double.Epsilon,
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetAttributeValue(Feature feature, string fieldName, out object? value)
    {
        if (feature.Attributes.TryGetValue(fieldName, out value))
        {
            return true;
        }

        foreach (var pair in feature.Attributes)
        {
            if (string.Equals(pair.Key, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryConvertToDouble(object? value, out double parsed)
    {
        switch (value)
        {
            case null:
                parsed = 0;
                return false;
            case JsonElement jsonElement:
                return TryConvertToDouble(jsonElement, out parsed);
            case byte byteValue:
                parsed = byteValue;
                return true;
            case short shortValue:
                parsed = shortValue;
                return true;
            case int intValue:
                parsed = intValue;
                return true;
            case long longValue:
                parsed = longValue;
                return true;
            case float floatValue:
                parsed = floatValue;
                return true;
            case double doubleValue:
                parsed = doubleValue;
                return true;
            case decimal decimalValue:
                parsed = (double)decimalValue;
                return true;
            case string stringValue:
                return double.TryParse(stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
            default:
                parsed = 0;
                return false;
        }
    }

    private static bool TryConvertToDouble(JsonElement value, out double parsed)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out parsed))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
        }

        parsed = 0;
        return false;
    }
}
