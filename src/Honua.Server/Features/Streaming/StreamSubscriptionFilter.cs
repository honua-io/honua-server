// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Queries.Filters;

namespace Honua.Server.Features.Streaming;

internal sealed class StreamSubscriptionFilter
{
    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyProperties =
        new Dictionary<string, JsonElement>(0, StringComparer.Ordinal);

    private readonly StreamSubscriberSecurity? _subscriberSecurity;
    private readonly string? _serviceId;
    private readonly string? _resolvedServiceId;
    private readonly StringComparison _serviceIdComparison;
    private readonly bool _hasExplicitLayerScope;
    private readonly int[]? _layerIds;
    private readonly double[]? _bbox;
    private readonly FilterExpression? _attributeFilter;
    private readonly StreamTemporalFilter? _temporalFilter;
    private readonly string[] _valueFilterFields;
    private readonly FeatureStreamRoutabilityGuard? _routabilityGuard;

    public StreamSubscriptionFilter(
        string? serviceId = null,
        bool serviceIdIsExact = false,
        string? resolvedServiceId = null,
        bool hasExplicitLayerScope = false,
        int[]? layerIds = null,
        double[]? bbox = null,
        FilterExpression? attributeFilter = null,
        StreamTemporalFilter? temporalFilter = null,
        FeatureStreamRoutabilityGuard? routabilityGuard = null,
        StreamSubscriberSecurity? subscriberSecurity = null)
    {
        _subscriberSecurity = subscriberSecurity;
        _serviceId = serviceId;
        _resolvedServiceId = resolvedServiceId;
        _hasExplicitLayerScope = hasExplicitLayerScope;
        _serviceIdComparison = serviceIdIsExact
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        _layerIds = layerIds;
        _bbox = bbox;
        _attributeFilter = attributeFilter;
        _temporalFilter = temporalFilter;
        _valueFilterFields = CollectValueFilterFields(attributeFilter, temporalFilter);
        _routabilityGuard = routabilityGuard;
    }

    public string? ServiceId => _serviceId;

    public string? ResolvedServiceId => _resolvedServiceId;

    public bool HasExplicitLayerScope => _hasExplicitLayerScope;

    public int[]? LayerIds => _layerIds;

    internal StreamSubscriberSecurity? SubscriberSecurity => _subscriberSecurity;

    internal string? FindMaskedFilterField()
        => _valueFilterFields.Length == 0 ? null : _subscriberSecurity?.FindMaskedField(_valueFilterFields);

    // A value filter is not convergent across baseline and deltas, because the leaving update is judged on the image after the edit and is not delivered.
    public bool HasValueDependentPredicate =>
        _subscriberSecurity?.HasRowPredicates == true ||
        _bbox is not null || _attributeFilter is not null || _temporalFilter is not null;

    public string Summary
    {
        get
        {
            var parts = new List<string>(4);
            if (_serviceId is not null)
            {
                parts.Add($"serviceId={_serviceId}");
            }

            if (_layerIds is not null)
            {
                parts.Add($"layers=[{string.Join(",", _layerIds)}]");
            }

            if (_bbox is not null)
            {
                parts.Add($"bbox=[{_bbox[0]},{_bbox[1]},{_bbox[2]},{_bbox[3]}]");
            }

            if (_attributeFilter is not null)
            {
                parts.Add("where=<expression>");
            }

            if (_temporalFilter is not null)
            {
                parts.Add("datetime=<interval>");
            }

            return string.Join("; ", parts);
        }
    }

    public bool Matches(FeatureStreamEnvelope envelope, double[]? geometryEnvelope, string? propertiesJson)
    {
        IReadOnlyDictionary<string, JsonElement>? parsedProperties = null;
        bool propertiesParsed = false;
        return Matches(envelope, geometryEnvelope, propertiesJson, ref parsedProperties, ref propertiesParsed);
    }

    // A change made directly in the database names no service; it reaches a subscription whose
    // service publishes the layer, or an unscoped subscription when any service publishes it.
    private bool MatchesService(FeatureStreamEnvelope envelope)
    {
        if (envelope.ServiceId.Length == 0)
        {
            return MatchesDatabaseChangeRoute(envelope.LayerId);
        }

        if (_routabilityGuard is not null &&
            !_routabilityGuard.IsRoutable(envelope.ServiceId, envelope.LayerId, _resolvedServiceId))
        {
            return false;
        }

        if (_resolvedServiceId is not null)
        {
            return _routabilityGuard is not null
                   || string.Equals(envelope.ServiceId, _resolvedServiceId, StringComparison.Ordinal);
        }

        return _serviceId is null || string.Equals(envelope.ServiceId, _serviceId, _serviceIdComparison);
    }

    // True when the routability snapshot, not the subscription's own criteria, turns this change
    // away. That snapshot can lag a publish by its refresh interval, so such a change must still be
    // read from the store rather than treated as offered.
    internal bool RejectedByRoutability(FeatureStreamEnvelope envelope)
    {
        if (_routabilityGuard is null)
        {
            return false;
        }

        if (envelope.ServiceId.Length > 0)
        {
            return !_routabilityGuard.IsRoutable(envelope.ServiceId, envelope.LayerId, _resolvedServiceId);
        }

        var subscribedService = _resolvedServiceId ?? _serviceId;
        return subscribedService is null
            ? !_routabilityGuard.IsLayerRoutable(envelope.LayerId)
            : !_routabilityGuard.IsRoutable(subscribedService, envelope.LayerId, _resolvedServiceId);
    }

    private bool MatchesDatabaseChangeRoute(int layerId)
    {
        var subscribedService = _resolvedServiceId ?? _serviceId;
        if (_routabilityGuard is null)
        {
            return subscribedService is null;
        }

        return subscribedService is null
            ? _routabilityGuard.IsLayerRoutable(layerId)
            : _routabilityGuard.IsRoutable(subscribedService, layerId, _resolvedServiceId);
    }

    internal bool Matches(
        FeatureStreamEnvelope envelope,
        double[]? geometryEnvelope,
        string? propertiesJson,
        ref IReadOnlyDictionary<string, JsonElement>? parsedProperties,
        ref bool propertiesParsed)
    {
        if (!MatchesService(envelope))
        {
            return false;
        }

        // 2. Layer filter.
        if (_layerIds is not null)
        {
            bool found = false;
            for (int i = 0; i < _layerIds.Length; i++)
            {
                if (_layerIds[i] == envelope.LayerId)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        if (_subscriberSecurity is not null && !AllowsSubscriber(envelope))
        {
            return false;
        }

        // Delete events pass client filters only after subscriber row authorization.
        // Missing before-images cannot authorize a delete under a row policy.
        bool isDelete = string.Equals(envelope.Operation, "delete", StringComparison.OrdinalIgnoreCase);
        if (isDelete)
        {
            return true;
        }

        // 3. Bbox filter — 4 double comparisons for envelope intersection.
        if (_bbox is not null)
        {
            if (geometryEnvelope is null || geometryEnvelope.Length < 4)
            {
                // Non-delete events without geometry cannot intersect a bbox.
                return false;
            }
            else if (!EnvelopesIntersect(_bbox, geometryEnvelope))
            {
                return false;
            }
        }

        // 4. Attribute/temporal filters — parse propertiesJson once and evaluate AST/window.
        if (_attributeFilter is not null || _temporalFilter is not null)
        {
            if (ReadsMaskedField(envelope))
            {
                return false;
            }

            if (!propertiesParsed)
            {
                propertiesParsed = true;
                if (propertiesJson is null)
                {
                    parsedProperties = EmptyProperties;
                }
                else
                {
                    try
                    {
                        parsedProperties = JsonSerializer.Deserialize(
                            propertiesJson,
                            FeatureStreamJsonContext.Default.DictionaryStringJsonElement) ?? EmptyProperties;
                    }
                    // Intentional catch-all: this filter predicate must be safe against malformed
                    // stored properties JSON on the hot per-event delivery path; any parse failure
                    // is treated as a non-match below rather than surfaced/thrown.
                    catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
                    {
                        // No logger here by design: this filter is a plain `new`-constructed,
                        // per-subscription value evaluated on the hot per-event delivery path
                        // (not DI-registered), so wiring a logger through would add a dependency
                        // to a type instantiated per subscription/message. Malformed properties
                        // JSON is treated as "does not match" below rather than surfaced/thrown,
                        // which is the safe/conservative outcome for a filter predicate.
                        parsedProperties = null;
                    }
                }
            }

            if (parsedProperties is null)
            {
                return false;
            }

            if (_attributeFilter is not null && !AttributeMatches(parsedProperties))
            {
                return false;
            }

            if (_temporalFilter is not null &&
                !MatchesTemporal(parsedProperties, _temporalFilter))
            {
                return false;
            }
        }

        return true;
    }

    // Field masks can change while the subscription is open, so the fields a client predicate
    // reads are checked against the event's current policy, not only at subscribe.
    private bool ReadsMaskedField(FeatureStreamEnvelope envelope)
        => _valueFilterFields.Length > 0 && _subscriberSecurity?.MasksAny(envelope, _valueFilterFields) == true;

    private bool AllowsSubscriber(FeatureStreamEnvelope envelope)
    {
        try
        {
            return _subscriberSecurity!.Allows(envelope);
        }
        catch (Exception ex) when (IsUnevaluableLike(ex))
        {
            return false;
        }
    }

    private bool AttributeMatches(IReadOnlyDictionary<string, JsonElement> properties)
    {
        try
        {
            return InMemoryFilterEvaluator.Evaluate(_attributeFilter!, properties);
        }
        catch (Exception ex) when (IsUnevaluableLike(ex))
        {
            return false;
        }
    }

    private static string[] CollectValueFilterFields(FilterExpression? attributeFilter, StreamTemporalFilter? temporalFilter)
    {
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (attributeFilter is not null)
        {
            fields.UnionWith(FeatureQuerySecurity.CollectFieldReferences(attributeFilter));
        }

        if (temporalFilter is not null)
        {
            fields.Add(temporalFilter.StartField);
            if (!string.IsNullOrWhiteSpace(temporalFilter.EndField))
            {
                fields.Add(temporalFilter.EndField);
            }
        }

        return [.. fields];
    }

    private static bool IsUnevaluableLike(Exception exception)
        => exception is UnevaluableLikePatternException or RegexMatchTimeoutException;

    internal FeatureStreamEnvelope Project(FeatureStreamEnvelope envelope)
    {
        if (envelope.ServiceId.Length == 0)
        {
            var serviceId = _resolvedServiceId ?? _serviceId ?? _routabilityGuard?.ResolveLayerServiceId(envelope.LayerId);
            if (serviceId is not null)
            {
                envelope = envelope with { ServiceId = serviceId, SourceId = serviceId };
            }
        }

        return _subscriberSecurity?.Project(envelope) ?? envelope;
    }

    private static bool MatchesTemporal(
        IReadOnlyDictionary<string, JsonElement> properties,
        StreamTemporalFilter filter)
    {
        if (!TryGetTemporalValue(properties, filter.StartField, out var eventStart))
        {
            return false;
        }

        var eventEnd = eventStart;
        if (!string.IsNullOrWhiteSpace(filter.EndField) &&
            TryGetTemporalValue(properties, filter.EndField, out var parsedEnd))
        {
            eventEnd = parsedEnd;
        }

        if (filter.Start.HasValue && eventEnd < filter.Start.Value)
        {
            return false;
        }

        if (filter.End.HasValue && eventStart > filter.End.Value)
        {
            return false;
        }

        return true;
    }

    private static bool TryGetTemporalValue(
        IReadOnlyDictionary<string, JsonElement> properties,
        string fieldName,
        out DateTimeOffset value)
    {
        foreach (var (key, element) in properties)
        {
            if (!string.Equals(key, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(
                    element.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out value))
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.Number &&
                element.TryGetInt64(out var epochMilliseconds))
            {
                value = DateTimeOffset.FromUnixTimeMilliseconds(epochMilliseconds);
                return true;
            }

            break;
        }

        value = default;
        return false;
    }

    private static bool EnvelopesIntersect(double[] a, double[] b)
    {
        // Two AABBs do NOT intersect when one is entirely to the left/right/above/below the other.
        return a[0] <= b[2] && a[2] >= b[0] && a[1] <= b[3] && a[3] >= b[1];
    }
}
