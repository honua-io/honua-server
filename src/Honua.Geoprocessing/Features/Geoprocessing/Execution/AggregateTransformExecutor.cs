// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Operation.Union;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>transform.aggregate</c> executor. Groups the input FeatureCollection by one
/// or more <c>groupBy</c> attributes and emits one feature per group, carrying the
/// group-key attributes plus the requested aggregate columns. Aggregate functions
/// (per output column, specified as <c>field:function:alias</c>): <c>count</c>,
/// <c>sum</c>, <c>min</c>, <c>max</c>, <c>mean</c>, <c>stddev</c>, <c>first</c>,
/// <c>collect</c>. An optional <c>geometry</c> aggregate (<c>union</c>,
/// <c>centroid</c>, <c>extent</c>) reduces each group's geometries to a single
/// representative geometry. Pure managed NetTopologySuite — no native dependency.
/// </summary>
/// <remarks>
/// Bounded memory: group state is accumulated incrementally — numeric aggregates
/// keep running scalars, not the member values; <c>collect</c> retains the member
/// values for that column and <c>union</c>/<c>extent</c>/<c>centroid</c> retain the
/// group geometries, so peak memory is proportional to the grouped output (plus the
/// already-decoded input, which is bounded by
/// <see cref="GeoprocessingExecutorOptions.MaxArtifactBytes"/>). When <c>groupBy</c>
/// is empty the whole stream collapses into a single group.
/// </remarks>
internal sealed class AggregateTransformExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "transform.aggregate";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var groupBy = inputs.TryGet("groupBy", out var g) && !string.IsNullOrWhiteSpace(g)
            ? Split(g!)
            : Array.Empty<string>();
        var specs = ParseAggregates(inputs);
        var geometryMode = ParseGeometryMode(inputs);

        var groups = new Dictionary<string, GroupState>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attributes = feature.Attributes ?? new AttributesTable();
            var key = BuildGroupKey(attributes, groupBy);
            if (!groups.TryGetValue(key, out var state))
            {
                state = new GroupState(groupBy, attributes, specs, geometryMode);
                groups[key] = state;
                order.Add(key);
            }

            state.Accumulate(feature, attributes);
        }

        var output = new List<IFeature>(groups.Count);
        foreach (var key in order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Add(groups[key].Build());
        }

        return output;
    }

    private static List<AggregateSpec> ParseAggregates(StepInputReader inputs)
    {
        // Format: semicolon-separated 'field:function[:alias]'. The count function
        // ignores its field (use '*:count' or 'anything:count'); alias defaults to
        // FUNCTION_field (COUNT for count).
        if (!inputs.TryGet("aggregates", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            // No explicit aggregates -> emit a plain group COUNT so the transform is
            // still meaningful when only group-by columns are requested.
            return [new AggregateSpec(AggregateKind.Count, string.Empty, "COUNT")];
        }

        var specs = new List<AggregateSpec>();
        foreach (var token in raw!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                throw new TransformInputException(
                    $"aggregate '{token}' must be 'field:function' or 'field:function:alias'");
            }

            var field = parts[0];
            var function = parts[1].ToLowerInvariant();
            var kind = function switch
            {
                "count" => AggregateKind.Count,
                "sum" => AggregateKind.Sum,
                "min" => AggregateKind.Min,
                "max" => AggregateKind.Max,
                "mean" or "avg" or "average" => AggregateKind.Mean,
                "stddev" or "std" => AggregateKind.StdDev,
                "first" => AggregateKind.First,
                "collect" => AggregateKind.Collect,
                _ => throw new TransformInputException(
                    $"aggregate function '{function}' is not supported " +
                    "(allowed: count, sum, min, max, mean, stddev, first, collect)"),
            };

            if (kind != AggregateKind.Count && string.IsNullOrWhiteSpace(field))
            {
                throw new TransformInputException($"aggregate function '{function}' requires a field");
            }

            var alias = parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2])
                ? parts[2]
                : DefaultAlias(kind, field);

            specs.Add(new AggregateSpec(kind, field, alias));
        }

        return specs;
    }

    private static string DefaultAlias(AggregateKind kind, string field) => kind switch
    {
        AggregateKind.Count => "COUNT",
        AggregateKind.Sum => "SUM_" + field,
        AggregateKind.Min => "MIN_" + field,
        AggregateKind.Max => "MAX_" + field,
        AggregateKind.Mean => "MEAN_" + field,
        AggregateKind.StdDev => "STDDEV_" + field,
        AggregateKind.First => "FIRST_" + field,
        AggregateKind.Collect => "COLLECT_" + field,
        _ => field,
    };

    private static GeometryMode ParseGeometryMode(StepInputReader inputs)
        => inputs.GetOrDefault("geometry", "none").Trim().ToLowerInvariant() switch
        {
            "none" or "" => GeometryMode.None,
            "union" => GeometryMode.Union,
            "centroid" => GeometryMode.Centroid,
            "extent" or "envelope" => GeometryMode.Extent,
            var other => throw new TransformInputException(
                $"geometry aggregate '{other}' is not supported (allowed: none, union, centroid, extent)"),
        };

    private static string BuildGroupKey(IAttributesTable attributes, string[] groupBy)
    {
        if (groupBy.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < groupBy.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('\u001f');
            }

            if (attributes.Exists(groupBy[i]))
            {
                sb.Append(Convert.ToString(attributes.GetOptionalValue(groupBy[i]), CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append("\u0000<null>");
            }
        }

        return sb.ToString();
    }

    private static string[] Split(string raw)
        => raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryReadNumeric(object? raw, out double value)
    {
        switch (raw)
        {
            case null: value = 0; return false;
            case double d: value = d; return true;
            case float fl: value = fl; return true;
            case int i: value = i; return true;
            case long l: value = l; return true;
            case short s: value = s; return true;
            case decimal m: value = (double)m; return true;
            default:
                return double.TryParse(
                    Convert.ToString(raw, CultureInfo.InvariantCulture),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value);
        }
    }

    private enum AggregateKind
    {
        Count,
        Sum,
        Min,
        Max,
        Mean,
        StdDev,
        First,
        Collect,
    }

    private enum GeometryMode
    {
        None,
        Union,
        Centroid,
        Extent,
    }

    private readonly record struct AggregateSpec(AggregateKind Kind, string Field, string Alias);

    /// <summary>Per-group running accumulator. Numeric state is incremental.</summary>
    private sealed class GroupState
    {
        private readonly string[] _groupBy;
        private readonly IReadOnlyList<AggregateSpec> _specs;
        private readonly GeometryMode _geometryMode;
        private readonly object?[] _groupKeyValues;

        private readonly long[] _counts;
        private readonly double[] _sums;
        private readonly double[] _sumSquares;
        private readonly double[] _mins;
        private readonly double[] _maxs;
        private readonly object?[] _firsts;
        private readonly bool[] _firstSet;
        private readonly List<object?>?[] _collected;
        private readonly List<NtsGeometry>? _geometries;

        public GroupState(
            string[] groupBy,
            IAttributesTable firstRowAttributes,
            IReadOnlyList<AggregateSpec> specs,
            GeometryMode geometryMode)
        {
            _groupBy = groupBy;
            _specs = specs;
            _geometryMode = geometryMode;

            _groupKeyValues = new object?[groupBy.Length];
            for (var i = 0; i < groupBy.Length; i++)
            {
                _groupKeyValues[i] = firstRowAttributes.Exists(groupBy[i])
                    ? firstRowAttributes.GetOptionalValue(groupBy[i])
                    : null;
            }

            var n = specs.Count;
            _counts = new long[n];
            _sums = new double[n];
            _sumSquares = new double[n];
            _mins = new double[n];
            _maxs = new double[n];
            _firsts = new object?[n];
            _firstSet = new bool[n];
            _collected = new List<object?>?[n];
            for (var i = 0; i < n; i++)
            {
                _mins[i] = double.PositiveInfinity;
                _maxs[i] = double.NegativeInfinity;
                if (specs[i].Kind == AggregateKind.Collect)
                {
                    _collected[i] = new List<object?>();
                }
            }

            _geometries = geometryMode == GeometryMode.None ? null : new List<NtsGeometry>();
        }

        public void Accumulate(IFeature feature, IAttributesTable attributes)
        {
            for (var i = 0; i < _specs.Count; i++)
            {
                var spec = _specs[i];
                if (spec.Kind == AggregateKind.Count)
                {
                    _counts[i]++;
                    continue;
                }

                var hasValue = attributes.Exists(spec.Field);
                var raw = hasValue ? attributes.GetOptionalValue(spec.Field) : null;

                switch (spec.Kind)
                {
                    case AggregateKind.First:
                        if (!_firstSet[i])
                        {
                            _firsts[i] = raw;
                            _firstSet[i] = true;
                        }

                        break;
                    case AggregateKind.Collect:
                        _collected[i]!.Add(raw);
                        break;
                    default:
                        if (TryReadNumeric(raw, out var numeric))
                        {
                            _counts[i]++;
                            _sums[i] += numeric;
                            _sumSquares[i] += numeric * numeric;
                            if (numeric < _mins[i]) _mins[i] = numeric;
                            if (numeric > _maxs[i]) _maxs[i] = numeric;
                        }

                        break;
                }
            }

            if (_geometries is not null)
            {
                var geometry = feature.Geometry;
                if (geometry is not null && !geometry.IsEmpty)
                {
                    _geometries.Add(geometry);
                }
            }
        }

        public Feature Build()
        {
            var table = new AttributesTable();
            for (var i = 0; i < _groupBy.Length; i++)
            {
                table.Add(_groupBy[i], _groupKeyValues[i]);
            }

            for (var i = 0; i < _specs.Count; i++)
            {
                var spec = _specs[i];
                object? value = spec.Kind switch
                {
                    AggregateKind.Count => _counts[i],
                    AggregateKind.Sum => _counts[i] > 0 ? _sums[i] : null,
                    AggregateKind.Min => _counts[i] > 0 ? _mins[i] : null,
                    AggregateKind.Max => _counts[i] > 0 ? _maxs[i] : null,
                    AggregateKind.Mean => _counts[i] > 0 ? _sums[i] / _counts[i] : null,
                    AggregateKind.StdDev => _counts[i] > 0 ? StdDev(i) : null,
                    AggregateKind.First => _firsts[i],
                    AggregateKind.Collect => CollectToString(_collected[i]!),
                    _ => null,
                };

                if (table.Exists(spec.Alias))
                {
                    table[spec.Alias] = value;
                }
                else
                {
                    table.Add(spec.Alias, value);
                }
            }

            return new Feature(BuildGeometry(), table);
        }

        private double StdDev(int i)
        {
            var count = _counts[i];

            // Population standard deviation: sqrt(E[x^2] - E[x]^2).
            var mean = _sums[i] / count;
            var variance = (_sumSquares[i] / count) - (mean * mean);
            return variance <= 0 ? 0d : Math.Sqrt(variance);
        }

        private static string CollectToString(List<object?> values)
            => string.Join(",", values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty));

        private NtsGeometry? BuildGeometry()
        {
            if (_geometries is null || _geometries.Count == 0)
            {
                return null;
            }

            var factory = _geometries[0].Factory;
            switch (_geometryMode)
            {
                case GeometryMode.Union:
                    return UnaryUnionOp.Union(_geometries);
                case GeometryMode.Centroid:
                    return UnaryUnionOp.Union(_geometries).Centroid;
                case GeometryMode.Extent:
                    {
                        var env = _geometries[0].EnvelopeInternal.Copy();
                        for (var i = 1; i < _geometries.Count; i++)
                        {
                            env.ExpandToInclude(_geometries[i].EnvelopeInternal);
                        }

                        return factory.ToGeometry(env);
                    }
                default:
                    return null;
            }
        }
    }
}
