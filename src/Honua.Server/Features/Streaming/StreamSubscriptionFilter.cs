// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Queries.Filters;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Composite subscription filter that evaluates in cheapest-first order:
/// layer filter, bbox filter, then attribute filter. Delete events pass
/// all non-layer filters (subscribers should always be notified of deletes
/// in their subscribed layers).
/// </summary>
internal sealed class StreamSubscriptionFilter : IStreamSubscriptionFilter
{
    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyProperties =
        new Dictionary<string, JsonElement>(0, StringComparer.Ordinal);

    private readonly string? _serviceId;
    private readonly int[]? _layerIds;
    private readonly double[]? _bbox;
    private readonly FilterExpression? _attributeFilter;
    private readonly StreamTemporalFilter? _temporalFilter;

    /// <summary>
    /// Creates a composite subscription filter.
    /// </summary>
    /// <param name="serviceId">Allowed service ID (null = all services).</param>
    /// <param name="layerIds">Allowed layer IDs (null = all layers).</param>
    /// <param name="bbox">Bounding box [MinX, MinY, MaxX, MaxY] (null = no spatial filter).</param>
    /// <param name="attributeFilter">Parsed filter expression (null = no attribute filter).</param>
    /// <param name="temporalFilter">Parsed temporal interval filter (null = no temporal filter).</param>
    public StreamSubscriptionFilter(
        string? serviceId = null,
        int[]? layerIds = null,
        double[]? bbox = null,
        FilterExpression? attributeFilter = null,
        StreamTemporalFilter? temporalFilter = null)
    {
        _serviceId = serviceId;
        _layerIds = layerIds;
        _bbox = bbox;
        _attributeFilter = attributeFilter;
        _temporalFilter = temporalFilter;
    }

    /// <summary>
    /// Service ID restriction, if any.
    /// </summary>
    public string? ServiceId => _serviceId;

    /// <summary>
    /// Layer restrictions, if any.
    /// </summary>
    public int[]? LayerIds => _layerIds;

    /// <inheritdoc />
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

    /// <inheritdoc />
    public bool Matches(FeatureStreamEnvelope envelope, double[]? geometryEnvelope, string? propertiesJson)
    {
        IReadOnlyDictionary<string, JsonElement>? parsedProperties = null;
        bool propertiesParsed = false;
        return Matches(envelope, geometryEnvelope, propertiesJson, ref parsedProperties, ref propertiesParsed);
    }

    internal bool Matches(
        FeatureStreamEnvelope envelope,
        double[]? geometryEnvelope,
        string? propertiesJson,
        ref IReadOnlyDictionary<string, JsonElement>? parsedProperties,
        ref bool propertiesParsed)
    {
        // 1. Service filter — cheapest, short-circuits first.
        if (_serviceId is not null &&
            !string.Equals(envelope.ServiceId, _serviceId, StringComparison.OrdinalIgnoreCase))
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

        // Delete events pass all non-layer filters. Subscribers should always
        // be notified when a feature in their layer is removed.
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
                    catch
                    {
                        parsedProperties = null;
                    }
                }
            }

            if (parsedProperties is null)
            {
                return false;
            }

            if (_attributeFilter is not null &&
                !InMemoryFilterEvaluator.Evaluate(_attributeFilter, parsedProperties))
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

    /// <summary>
    /// Tests whether two axis-aligned bounding boxes intersect.
    /// Both arrays are [MinX, MinY, MaxX, MaxY].
    /// </summary>
    private static bool EnvelopesIntersect(double[] a, double[] b)
    {
        // Two AABBs do NOT intersect when one is entirely to the left/right/above/below the other.
        return a[0] <= b[2] && a[2] >= b[0] && a[1] <= b[3] && a[3] >= b[1];
    }
}
