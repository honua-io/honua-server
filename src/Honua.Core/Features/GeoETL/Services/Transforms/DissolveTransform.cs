// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using NetTopologySuite.Features;
using NetTopologySuite.Operation.Union;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Core.Features.GeoETL.Services.Transforms;

/// <summary>
/// Group-aware dissolve transform — the managed, layer-scope counterpart of the
/// ArcGIS <c>Dissolve_management</c> tool. Buffers the streamed input by group
/// key (one or more attribute fields, or a single "dissolve-all" bucket when no
/// field is supplied), unions each group's geometries with NetTopologySuite
/// <see cref="UnaryUnionOp"/>, and emits ONE feature per group whose attributes
/// carry the group-by field values plus optional summary statistics.
/// </summary>
/// <remarks>
/// Unlike the per-feature <see cref="GeometryOperationTransform"/>, dissolve is an
/// inherently buffering (group-by) operation: it materializes the whole input
/// before producing output. That is acceptable because layer-scope dissolve runs
/// as a batch JOB on the durable substrate, not a per-request call. Pure managed
/// NetTopologySuite — no GEOS/GDAL native dependency.
///
/// Options:
/// <list type="bullet">
/// <item><c>groupByFields</c> — comma-separated attribute field names. When empty
/// or omitted, every input feature collapses into a single group ("dissolve all"),
/// matching <c>Dissolve_management</c> with no dissolve field.</item>
/// <item><c>statistics</c> — optional summary specification, a semicolon-separated
/// list of <c>field:stat</c> pairs. Supported stats: <c>count</c> (field ignored;
/// emitted once per group as <c>COUNT</c>), <c>sum</c>, <c>mean</c>, <c>min</c>,
/// <c>max</c> (numeric fields), and <c>first</c> (the first encountered value of a
/// field). Output attribute names follow the ArcGIS convention <c>STAT_field</c>
/// (e.g. <c>SUM_pop</c>, <c>MEAN_area</c>), with the bare group COUNT emitted as
/// <c>COUNT</c>.</item>
/// </list>
/// A group whose unioned geometry is empty is dropped, matching the overlay
/// transforms and PostGIS semantics.
/// </remarks>
public sealed class DissolveTransform : IPipelineTransform
{
    /// <summary>
    /// The transform type discriminator.
    /// </summary>
    public const string TransformType = "dissolve";

    private const string DefaultGroupKey = "__all__";

    // Unit-separator control char between group-key parts so distinct field
    // boundaries never collide (matching DedupTransform's key composition).
    private const char Separator = '\u001F';
    private const char NullMarker = '\u00A0';

    /// <inheritdoc />
    public string Type => TransformType;

    /// <inheritdoc />
    public async IAsyncEnumerable<IFeature> TransformAsync(
        TransformConfig config,
        IAsyncEnumerable<IFeature> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(source);

        var groupByFields = ReadGroupByFields(config);
        var stats = ReadStatistics(config);

        // Group-by requires buffering; preserve first-seen order so the output is
        // deterministic for tests and downstream consumers.
        var groups = new Dictionary<string, GroupAccumulator>(StringComparer.Ordinal);
        var orderedKeys = new List<string>();

        await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                continue;
            }

            var key = BuildGroupKey(feature, groupByFields);
            if (!groups.TryGetValue(key, out var accumulator))
            {
                accumulator = new GroupAccumulator(groupByFields.Length);
                groups[key] = accumulator;
                orderedKeys.Add(key);
            }

            accumulator.Add(feature, groupByFields, stats);
        }

        foreach (var key in orderedKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var accumulator = groups[key];
            var unioned = UnaryUnionOp.Union(accumulator.Geometries);
            if (unioned is null || unioned.IsEmpty)
            {
                continue;
            }

            // Carry the SRID of the first geometry in the group (all features in a
            // layer share one CRS).
            unioned.SRID = accumulator.Srid;

            var attributes = new AttributesTable();

            // Group-by field values identify the group.
            for (var i = 0; i < groupByFields.Length; i++)
            {
                attributes.Add(groupByFields[i], accumulator.GroupValues[i]);
            }

            // Summary statistics. The bare per-group COUNT is always available and
            // emitted as "COUNT" when requested; field-scoped stats use the ArcGIS
            // STAT_field naming convention.
            foreach (var spec in stats)
            {
                var (name, value) = accumulator.Resolve(spec);
                if (attributes.Exists(name))
                {
                    attributes[name] = value;
                }
                else
                {
                    attributes.Add(name, value);
                }
            }

            yield return new Feature(unioned, attributes);
        }
    }

    private static string[] ReadGroupByFields(TransformConfig config)
    {
        if (config.Options.TryGetValue("groupByFields", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return [];
    }

    private static List<StatSpec> ReadStatistics(TransformConfig config)
    {
        if (!config.Options.TryGetValue("statistics", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var specs = new List<StatSpec>();
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
                "first" => StatKind.First,
                _ => throw new InvalidOperationException(
                    $"Dissolve transform does not support statistic '{statName}'. " +
                    "Supported: count, sum, mean, min, max, first.")
            };

            if (kind != StatKind.Count && string.IsNullOrWhiteSpace(field))
            {
                throw new InvalidOperationException(
                    $"Dissolve statistic '{statName}' requires a field, e.g. 'fieldName:{statName}'.");
            }

            specs.Add(new StatSpec(kind, field, OutputName(kind, field)));
        }

        return specs;
    }

    private static string OutputName(StatKind kind, string field) => kind switch
    {
        StatKind.Count => "COUNT",
        StatKind.Sum => "SUM_" + field,
        StatKind.Mean => "MEAN_" + field,
        StatKind.Min => "MIN_" + field,
        StatKind.Max => "MAX_" + field,
        StatKind.First => "FIRST_" + field,
        _ => field
    };

    private static string BuildGroupKey(IFeature feature, string[] groupByFields)
    {
        if (groupByFields.Length == 0)
        {
            return DefaultGroupKey;
        }

        var builder = new StringBuilder();
        var attributes = feature.Attributes;
        foreach (var field in groupByFields)
        {
            var value = attributes is not null && attributes.Exists(field)
                ? Convert.ToString(attributes.GetOptionalValue(field), CultureInfo.InvariantCulture)
                : null;
            builder.Append(value ?? NullMarker.ToString());
            builder.Append(Separator);
        }

        return builder.ToString();
    }

    private static bool TryReadNumeric(IFeature feature, string field, out double value)
    {
        value = 0;
        var attributes = feature.Attributes;
        if (attributes is null || !attributes.Exists(field))
        {
            return false;
        }

        var raw = attributes.GetOptionalValue(field);
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

    private enum StatKind
    {
        Count,
        Sum,
        Mean,
        Min,
        Max,
        First
    }

    private readonly record struct StatSpec(StatKind Kind, string Field, string OutputName);

    private sealed class GroupAccumulator
    {
        private readonly Dictionary<string, double> _sums = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _mins = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _maxs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, object?> _firsts = new(StringComparer.Ordinal);

        public GroupAccumulator(int groupFieldCount)
        {
            GroupValues = new object?[groupFieldCount];
        }

        public List<NtsGeometry> Geometries { get; } = new();

        public object?[] GroupValues { get; }

        public int Srid { get; private set; }

        public long FeatureCount { get; private set; }

        public void Add(IFeature feature, string[] groupByFields, IReadOnlyList<StatSpec> stats)
        {
            if (FeatureCount == 0)
            {
                Srid = feature.Geometry!.SRID;
                for (var i = 0; i < groupByFields.Length; i++)
                {
                    GroupValues[i] = feature.Attributes is not null && feature.Attributes.Exists(groupByFields[i])
                        ? feature.Attributes.GetOptionalValue(groupByFields[i])
                        : null;
                }
            }

            Geometries.Add(feature.Geometry!);
            FeatureCount++;

            // Accumulate each distinct field at most once per feature even when
            // several specs reference the same field (e.g. pop:sum + pop:mean),
            // so the running sum/min/max are not multiplied by the spec count.
            var numericSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var spec in stats)
            {
                switch (spec.Kind)
                {
                    case StatKind.Count:
                        break; // FeatureCount covers it.
                    case StatKind.First:
                        if (!_firsts.ContainsKey(spec.Field))
                        {
                            _firsts[spec.Field] = feature.Attributes is not null && feature.Attributes.Exists(spec.Field)
                                ? feature.Attributes.GetOptionalValue(spec.Field)
                                : null;
                        }

                        break;
                    default:
                        if (numericSeen.Add(spec.Field) && TryReadNumeric(feature, spec.Field, out var numeric))
                        {
                            _sums[spec.Field] = _sums.GetValueOrDefault(spec.Field) + numeric;
                            _counts[spec.Field] = _counts.GetValueOrDefault(spec.Field) + 1;
                            _mins[spec.Field] = _mins.TryGetValue(spec.Field, out var min) ? Math.Min(min, numeric) : numeric;
                            _maxs[spec.Field] = _maxs.TryGetValue(spec.Field, out var max) ? Math.Max(max, numeric) : numeric;
                        }

                        break;
                }
            }
        }

        public (string Name, object? Value) Resolve(StatSpec spec)
        {
            switch (spec.Kind)
            {
                case StatKind.Count:
                    return (spec.OutputName, FeatureCount);
                case StatKind.Sum:
                    return (spec.OutputName, _sums.TryGetValue(spec.Field, out var sum) ? sum : null);
                case StatKind.Mean:
                    return (spec.OutputName,
                        _counts.TryGetValue(spec.Field, out var c) && c > 0
                            ? _sums[spec.Field] / c
                            : null);
                case StatKind.Min:
                    return (spec.OutputName, _mins.TryGetValue(spec.Field, out var min) ? min : null);
                case StatKind.Max:
                    return (spec.OutputName, _maxs.TryGetValue(spec.Field, out var max) ? max : null);
                case StatKind.First:
                    return (spec.OutputName, _firsts.GetValueOrDefault(spec.Field));
                default:
                    return (spec.OutputName, null);
            }
        }
    }
}
