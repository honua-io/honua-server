// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

internal static class StatisticsSupport
{
    public readonly record struct StatSpec(StatKind Kind, string Field, string OutputName);

    public enum StatKind
    {
        Count,

        Sum,

        Mean,

        Min,

        Max,

        StdDev,

        Variance,

        CountValues,
    }

    public static List<StatSpec> ParseOutStatistics(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var trimmed = raw.TrimStart();
        var specs = trimmed.StartsWith('[') || trimmed.StartsWith('{')
            ? ParseGeoServicesStatistics(trimmed)
            : ParseStatistics(raw);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in specs.Where(spec => !names.Add(spec.OutputName)))
        {
            throw new TransformInputException(
                $"'outStatistics' requests output name '{spec.OutputName}' more than once; give each aggregate a distinct outStatisticFieldName.");
        }

        return specs;
    }

    public static void EnsureNoOutputCollisions(IReadOnlyList<StatSpec> stats, IReadOnlyCollection<string> reservedNames)
    {
        var reserved = new HashSet<string>(reservedNames, StringComparer.OrdinalIgnoreCase);
        foreach (var spec in stats.Where(spec => reserved.Contains(spec.OutputName)))
        {
            throw new TransformInputException(
                $"'outStatistics' output name '{spec.OutputName}' collides with a column this operation already emits; choose a different outStatisticFieldName.");
        }
    }

    private static List<StatSpec> ParseGeoServicesStatistics(string json)
    {
        // Same wrapping rule as the synchronous handler and ProcessPlanValidator: a single
        // statistics object is treated as a one-element array.
        var arrayJson = json.StartsWith('[') ? json : $"[{json}]";
        const string shapeError =
            "'outStatistics' must be a GeoServices statistics array whose entries each carry string " +
            "statisticType, onStatisticField and outStatisticFieldName values.";

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(arrayJson);
        }
        catch (JsonException)
        {
            throw new TransformInputException(shapeError);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new TransformInputException(shapeError);
            }

            var specs = new List<StatSpec>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !TryReadString(element, "statisticType", out var statisticType)
                    || !TryReadString(element, "onStatisticField", out var field)
                    || !TryReadString(element, "outStatisticFieldName", out var outputName))
                {
                    throw new TransformInputException(shapeError);
                }

                // Mirrors SpatialAnalyticsRequestHandlers.TryParseStatisticType.
                var kind = statisticType.ToLowerInvariant() switch
                {
                    "count" => StatKind.CountValues,
                    "sum" => StatKind.Sum,
                    "min" => StatKind.Min,
                    "max" => StatKind.Max,
                    "avg" => StatKind.Mean,
                    "stddev" => StatKind.StdDev,
                    "var" => StatKind.Variance,
                    _ => throw new TransformInputException(
                        $"statisticType '{statisticType}' is not supported (allowed: count, sum, min, max, avg, stddev, var)"),
                };

                specs.Add(new StatSpec(kind, field.Trim(), outputName.Trim()));
            }

            return specs;
        }
    }

    private static bool TryReadString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    public static List<StatSpec> ParseStatistics(string? raw)
    {
        var specs = new List<StatSpec>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return specs;
        }

        // Not a .Select(...) candidate: each token goes through multi-step parsing with
        // an explicit validation throw, which reads better as an imperative loop than a
        // Select lambda with an embedded throw.
        foreach (var parts in (raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Select(token => token.Split(':', StringSplitOptions.TrimEntries)))
        {
            var statName = (parts.Length >= 2 ? parts[1] : parts[0]).ToLowerInvariant();
            var field = parts.Length >= 2 ? parts[0] : string.Empty;

            var kind = statName switch
            {
                "count" => StatKind.Count,
                "sum" => StatKind.Sum,
                "mean" or "avg" or "average" => StatKind.Mean,
                "min" => StatKind.Min,
                "max" => StatKind.Max,
                "stddev" or "std" => StatKind.StdDev,
                _ => throw new TransformInputException(
                    $"statistic '{statName}' is not supported (allowed: count, sum, mean, min, max, stddev)"),
            };

            if (kind != StatKind.Count && string.IsNullOrWhiteSpace(field))
            {
                throw new TransformInputException(
                    $"statistic '{statName}' requires a field, e.g. 'fieldName:{statName}'");
            }

            specs.Add(new StatSpec(kind, field, OutputName(kind, field)));
        }

        return specs;
    }

    public static string OutputName(StatKind kind, string field) => kind switch
    {
        StatKind.Count => "FREQUENCY",
        StatKind.Sum => "SUM_" + field,
        StatKind.Mean => "MEAN_" + field,
        StatKind.Min => "MIN_" + field,
        StatKind.Max => "MAX_" + field,
        StatKind.StdDev => "STDDEV_" + field,
        _ => field,
    };

    public static List<string> ParseFieldList(string? raw)
    {
        var fields = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fields;
        }

        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            fields.Add(token);
        }

        return fields;
    }

    public static string GroupKey(IFeature feature, IReadOnlyList<string> caseFields)
    {
        if (caseFields.Count == 0)
        {
            return string.Empty;
        }

        // Use a delimiter unlikely to appear in attribute values; key is internal only.
        return string.Join('', caseFields.Select(f => ReadKeyComponent(feature, f)));
    }

    public static string ReadKeyComponent(IFeature feature, string field)
    {
        if (feature.Attributes is null || !feature.Attributes.Exists(field))
        {
            return "\0NULL";
        }

        var value = feature.Attributes.GetOptionalValue(field);
        return value is null ? "\0NULL" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "\0NULL";
    }

    // Each field is accumulated once even when several stats name it, or a sum would be multiplied by the number of co-requested stats.
    public static void Accumulate(
        IFeature feature,
        IReadOnlyList<StatSpec> stats,
        Dictionary<string, FieldAccumulator> accumulators)
    {
        foreach (var field in stats
            .Where(spec => spec.Kind != StatKind.Count)
            .Select(spec => spec.Field)
            .Distinct(StringComparer.Ordinal))
        {
            if (!accumulators.TryGetValue(field, out var accumulator))
            {
                accumulator = new FieldAccumulator();
                accumulators[field] = accumulator;
            }

            if (feature.Attributes is not null
                && feature.Attributes.Exists(field)
                && feature.Attributes.GetOptionalValue(field) is not null)
            {
                accumulator.AddPresent();
            }

            if (TryReadNumeric(feature, field, out var value))
            {
                accumulator.Add(value);
            }
        }
    }

    public static bool TryReadNumeric(IFeature feature, string field, out double value)
    {
        value = 0d;
        if (feature.Attributes is null || !feature.Attributes.Exists(field))
        {
            return false;
        }

        var raw = feature.Attributes.GetOptionalValue(field);
        switch (raw)
        {
            case null:
                return false;
            case double d:
                value = d;
                return true;
            case float f:
                value = f;
                return true;
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            case short s:
                value = s;
                return true;
            case decimal m:
                value = (double)m;
                return true;
            default:
                return double.TryParse(
                    Convert.ToString(raw, CultureInfo.InvariantCulture),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value);
        }
    }

    public sealed class FieldAccumulator
    {
        private long _count;
        private long _presentCount;
        private double _sum;
        private double _sumSquares;
        private double _min = double.PositiveInfinity;
        private double _max = double.NegativeInfinity;

        public void Add(double value)
        {
            _count++;
            _sum += value;
            _sumSquares += value * value;
            if (value < _min)
            {
                _min = value;
            }

            if (value > _max)
            {
                _max = value;
            }
        }

        public void AddPresent() => _presentCount++;

        public object? Resolve(StatKind kind)
        {
            switch (kind)
            {
                case StatKind.Sum:
                    return _count > 0 ? _sum : null;
                case StatKind.Mean:
                    return _count > 0 ? _sum / _count : null;
                case StatKind.Min:
                    return _count > 0 ? _min : null;
                case StatKind.Max:
                    return _count > 0 ? _max : null;
                case StatKind.StdDev:
                    // Sample standard deviation is undefined for n < 2.
                    return SampleVariance() is { } variance ? Math.Sqrt(variance) : null;
                case StatKind.Variance:
                    return SampleVariance();
                case StatKind.CountValues:
                    return _presentCount;
                default:
                    return _count;
            }
        }

        private double? SampleVariance()
        {
            if (_count < 2)
            {
                return null;
            }

            var mean = _sum / _count;
            var variance = (_sumSquares - (_count * mean * mean)) / (_count - 1);
            // Guard tiny negative from floating-point cancellation.
            return variance < 0d ? 0d : variance;
        }
    }
}
