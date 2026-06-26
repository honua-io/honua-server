// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Shared aggregation primitives for the managed statistics/summarization tool pack
/// <c>statistics.summarize</c>, <c>statistics.frequency</c> and
/// <c>statistics.calculate</c> (#2140). Provides numeric-field accumulation
/// (SUM/MEAN/MIN/MAX/COUNT and sample STDDEV), case-field group keys, and parsing
/// of the shared <c>field:stat</c> statistics descriptor. Null and non-numeric
/// values are skipped from numeric aggregates (matching Esri Summary Statistics);
/// the COUNT/FREQUENCY of a group counts every contributing row regardless.
/// </summary>
internal static class StatisticsSupport
{
    /// <summary>A single requested aggregate over a field.</summary>
    public readonly record struct StatSpec(StatKind Kind, string Field, string OutputName);

    /// <summary>Supported aggregate kinds.</summary>
    public enum StatKind
    {
        /// <summary>Count of contributing rows.</summary>
        Count,

        /// <summary>Sum of numeric values.</summary>
        Sum,

        /// <summary>Arithmetic mean of numeric values.</summary>
        Mean,

        /// <summary>Minimum numeric value.</summary>
        Min,

        /// <summary>Maximum numeric value.</summary>
        Max,

        /// <summary>Sample (n-1) standard deviation of numeric values.</summary>
        StdDev,
    }

    /// <summary>
    /// Parses a semicolon-separated <c>field:stat</c> descriptor into stat specs.
    /// Throws <see cref="TransformInputException"/> for an unknown statistic or a
    /// non-count statistic missing its field.
    /// </summary>
    public static List<StatSpec> ParseStatistics(string? raw)
    {
        var specs = new List<StatSpec>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return specs;
        }

        foreach (var token in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split(':', StringSplitOptions.TrimEntries);
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

    /// <summary>Canonical output column name for an aggregate.</summary>
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

    /// <summary>Splits a comma-separated field list, dropping blanks.</summary>
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

    /// <summary>Builds a composite group key from a feature's case-field values.</summary>
    public static string GroupKey(IFeature feature, IReadOnlyList<string> caseFields)
    {
        if (caseFields.Count == 0)
        {
            return string.Empty;
        }

        // Use a delimiter unlikely to appear in attribute values; key is internal only.
        return string.Join('', caseFields.Select(f => ReadKeyComponent(feature, f)));
    }

    /// <summary>Reads a feature's case-field value as a key component (null -> sentinel).</summary>
    public static string ReadKeyComponent(IFeature feature, string field)
    {
        if (feature.Attributes is null || !feature.Attributes.Exists(field))
        {
            return "\0NULL";
        }

        var value = feature.Attributes.GetOptionalValue(field);
        return value is null ? "\0NULL" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "\0NULL";
    }

    /// <summary>Reads a numeric field value, returning false for null/non-numeric.</summary>
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

    /// <summary>Accumulates numeric statistics for a single field within a group.</summary>
    public sealed class FieldAccumulator
    {
        private long _count;
        private double _sum;
        private double _sumSquares;
        private double _min = double.PositiveInfinity;
        private double _max = double.NegativeInfinity;

        /// <summary>Adds a numeric sample.</summary>
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

        /// <summary>Resolves an aggregate value (null when undefined for the sample).</summary>
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
                    if (_count < 2)
                    {
                        return null; // Sample standard deviation is undefined for n < 2.
                    }

                    var mean = _sum / _count;
                    var variance = (_sumSquares - (_count * mean * mean)) / (_count - 1);
                    if (variance < 0d)
                    {
                        variance = 0d; // Guard tiny negative from floating-point cancellation.
                    }

                    return Math.Sqrt(variance);
                default:
                    return _count;
            }
        }
    }
}
