// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Index.Strtree;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Server.Features.Geoprocessing.Execution;

/// <summary>
/// <c>analytics.spatial-join-managed</c> executor. A job-dispatchable, managed
/// (NetTopologySuite) spatial join that the workflow/codemod path can reach.
///
/// This is deliberately a DISTINCT process id from trunk's
/// <c>analytics.spatial-join</c>, which executes ONLY synchronously through the
/// layer-scoped PostGIS <c>SpatialAnalytics</c> protocol (Pro-gated, not routed
/// through the geoprocessing job dispatcher). Rather than collide with that
/// protocol id, this id exposes the same aggregating one-to-one summarizing
/// shape over two inline FeatureCollections so a workflow node / codemod can run
/// it as a job with no Postgres dependency.
///
/// Logic ported from the <c>SpatialJoinTransform</c> aggregate mode on
/// <c>feat/gp-layer-execution</c>: for each target feature it queries the join
/// set through an in-memory <see cref="STRtree{T}"/> spatial index and summarizes
/// EVERY matched join feature into per-target statistics — <c>JOIN_COUNT</c> plus
/// optional numeric <c>SUM_/MEAN_/MIN_/MAX_</c> aggregates. Targets with zero
/// matches are preserved with a <c>JOIN_COUNT</c> of 0 and null aggregates. Pure
/// managed overlay — no GEOS native dependency.
///
/// Both feature sets are supplied INLINE as <c>data:application/geo+json;base64</c>
/// data URIs (the <c>input</c> target set and the <c>join</c> reference set),
/// mirroring the GeoETL transform input model. Resolving a join set by catalog
/// layer id is deferred for the same reason as <c>sink.honua-layer</c>: it needs
/// the catalog NpgsqlDataSource, which would break the dispatcher's unconditional
/// handler construction in lean deploys without Postgres — a conditional-
/// registration follow-on.
/// </summary>
internal sealed class ManagedSpatialJoinExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "analytics.spatial-join-managed";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var predicate = ReadPredicate(inputs);
        var stats = ReadStatistics(inputs);

        var joinUri = inputs.Require("join");
        if (!FeatureCollectionArtifact.TryParseDataUri(joinUri, out var joinFeatures, out var joinError))
        {
            throw new TransformInputException($"'join' {joinError}");
        }

        var index = BuildIndex(joinFeatures, cancellationToken);

        var output = new List<IFeature>(source.Count);
        foreach (var target in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Add(Summarize(target, index, predicate, stats));
        }

        return output;
    }

    private static STRtree<IFeature> BuildIndex(
        FeatureCollection joinFeatures,
        CancellationToken cancellationToken)
    {
        var index = new STRtree<IFeature>();
        foreach (var feature in joinFeatures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = feature.Geometry;
            if (geometry is not null && !geometry.IsEmpty)
            {
                index.Insert(geometry.EnvelopeInternal, feature);
            }
        }

        index.Build();
        return index;
    }

    private static Feature Summarize(
        IFeature target,
        STRtree<IFeature> index,
        SpatialPredicate predicate,
        IReadOnlyList<StatSpec> stats)
    {
        var merged = new AttributesTable();
        if (target.Attributes is not null)
        {
            foreach (var name in target.Attributes.GetNames())
            {
                merged.Add(name, target.Attributes.GetOptionalValue(name));
            }
        }

        var accumulator = new StatAccumulator(stats);
        var geometry = target.Geometry;
        if (geometry is not null && !geometry.IsEmpty)
        {
            foreach (var candidate in index.Query(geometry.EnvelopeInternal))
            {
                if (Matches(candidate.Geometry, geometry, predicate))
                {
                    accumulator.Add(candidate);
                }
            }
        }

        // JOIN_COUNT is always emitted so a zero-match target is distinguishable.
        Upsert(merged, "JOIN_COUNT", accumulator.Count);
        foreach (var spec in stats)
        {
            if (spec.Kind == StatKind.Count)
            {
                continue; // JOIN_COUNT already covers the match count.
            }

            var (name, value) = accumulator.Resolve(spec);
            Upsert(merged, name, value);
        }

        return new Feature(target.Geometry, merged);
    }

    private static void Upsert(AttributesTable table, string name, object? value)
    {
        if (table.Exists(name))
        {
            table[name] = value;
        }
        else
        {
            table.Add(name, value);
        }
    }

    private static bool Matches(NtsGeometry? joinGeometry, NtsGeometry targetGeometry, SpatialPredicate predicate)
    {
        if (joinGeometry is null)
        {
            return false;
        }

        return predicate switch
        {
            // contains: the join geometry contains the target — the classic
            // point-in-polygon case.
            SpatialPredicate.Contains => joinGeometry.Contains(targetGeometry),
            // within: the target contains the join geometry.
            SpatialPredicate.Within => targetGeometry.Contains(joinGeometry),
            _ => joinGeometry.Intersects(targetGeometry),
        };
    }

    private static SpatialPredicate ReadPredicate(StepInputReader inputs)
    {
        var raw = inputs.GetOrDefault("predicate", "intersects");
        return raw.Trim().ToLowerInvariant() switch
        {
            "intersects" => SpatialPredicate.Intersects,
            "contains" => SpatialPredicate.Contains,
            "within" => SpatialPredicate.Within,
            _ => throw new TransformInputException(
                $"predicate '{raw}' is not supported (allowed: intersects, contains, within)"),
        };
    }

    private static List<StatSpec> ReadStatistics(StepInputReader inputs)
    {
        if (!inputs.TryGet("statistics", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var specs = new List<StatSpec>();
        foreach (var token in raw!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
                _ => throw new TransformInputException(
                    $"statistic '{statName}' is not supported (allowed: count, sum, mean, min, max)"),
            };

            if (kind != StatKind.Count && string.IsNullOrWhiteSpace(field))
            {
                throw new TransformInputException(
                    $"statistic '{statName}' requires a join field, e.g. 'fieldName:{statName}'");
            }

            specs.Add(new StatSpec(kind, field, OutputName(kind, field)));
        }

        return specs;
    }

    private static string OutputName(StatKind kind, string field) => kind switch
    {
        StatKind.Count => "JOIN_COUNT",
        StatKind.Sum => "SUM_" + field,
        StatKind.Mean => "MEAN_" + field,
        StatKind.Min => "MIN_" + field,
        StatKind.Max => "MAX_" + field,
        _ => field,
    };

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

    private enum SpatialPredicate
    {
        Intersects,
        Contains,
        Within,
    }

    private enum StatKind
    {
        Count,
        Sum,
        Mean,
        Min,
        Max,
    }

    private readonly record struct StatSpec(StatKind Kind, string Field, string OutputName);

    private sealed class StatAccumulator
    {
        private readonly Dictionary<string, double> _sums = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _mins = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _maxs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _fields;

        public StatAccumulator(IReadOnlyList<StatSpec> stats)
        {
            _fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var spec in stats)
            {
                if (spec.Kind != StatKind.Count && !string.IsNullOrWhiteSpace(spec.Field))
                {
                    _fields.Add(spec.Field);
                }
            }
        }

        public long Count { get; private set; }

        public void Add(IFeature joinFeature)
        {
            Count++;
            foreach (var field in _fields)
            {
                if (TryReadNumeric(joinFeature, field, out var numeric))
                {
                    _sums[field] = _sums.GetValueOrDefault(field) + numeric;
                    _counts[field] = _counts.GetValueOrDefault(field) + 1;
                    _mins[field] = _mins.TryGetValue(field, out var min) ? Math.Min(min, numeric) : numeric;
                    _maxs[field] = _maxs.TryGetValue(field, out var max) ? Math.Max(max, numeric) : numeric;
                }
            }
        }

        public (string Name, object? Value) Resolve(StatSpec spec)
        {
            switch (spec.Kind)
            {
                case StatKind.Sum:
                    return (spec.OutputName, _sums.TryGetValue(spec.Field, out var sum) ? sum : null);
                case StatKind.Mean:
                    return (spec.OutputName,
                        _counts.TryGetValue(spec.Field, out var c) && c > 0 ? _sums[spec.Field] / c : null);
                case StatKind.Min:
                    return (spec.OutputName, _mins.TryGetValue(spec.Field, out var min) ? min : null);
                case StatKind.Max:
                    return (spec.OutputName, _maxs.TryGetValue(spec.Field, out var max) ? max : null);
                default:
                    return (spec.OutputName, Count);
            }
        }
    }
}
