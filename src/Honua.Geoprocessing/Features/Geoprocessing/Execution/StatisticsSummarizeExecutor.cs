// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>statistics.summarize</c> executor (#2140). Computes per-group summary
/// statistics over one or more <c>caseFields</c>, matching Esri's
/// <c>Summary Statistics</c>. Emits a TABLE (null-geometry FeatureCollection) with
/// one row per distinct case-field combination, carrying the case-field values, a
/// <c>FREQUENCY</c> row count, and every requested <c>statistics</c> aggregate
/// (<c>SUM_/MEAN_/MIN_/MAX_/STDDEV_&lt;field&gt;</c>). Null/non-numeric values are
/// skipped from numeric aggregates; a null case value forms its own group. When no
/// case fields are supplied, a single all-rows summary row is produced. The
/// <c>input</c> layer is supplied inline as a <c>data:application/geo+json;base64</c>
/// data URI. Pure managed — no GDAL/GEOS native dependency.
/// </summary>
internal sealed class StatisticsSummarizeExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    /// <summary>The canonical process id this executor handles.</summary>
    internal const string HandledProcessId = "statistics.summarize";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var caseFields = StatisticsSupport.ParseFieldList(inputs.GetOrDefault("caseFields", string.Empty));
        var stats = StatisticsSupport.ParseStatistics(inputs.GetOrDefault("statistics", string.Empty));
        var numericFields = NumericFields(stats);

        // Preserve first-seen group order for stable output.
        var order = new List<string>();
        var groups = new Dictionary<string, GroupState>(StringComparer.Ordinal);

        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = StatisticsSupport.GroupKey(feature, caseFields);
            if (!groups.TryGetValue(key, out var state))
            {
                state = new GroupState(feature, caseFields, numericFields);
                groups.Add(key, state);
                order.Add(key);
            }

            state.Accumulate(feature, numericFields);
        }

        var output = new List<IFeature>(order.Count);
        foreach (var key in order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Add(groups[key].ToRow(caseFields, stats));
        }

        return output;
    }

    private static List<string> NumericFields(IReadOnlyList<StatisticsSupport.StatSpec> stats)
    {
        var fields = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Not a .Where(...) candidate: seen.Add(spec.Field) is the dedup side effect
        // itself, so a filter predicate here would double as the mutation.
        foreach (var spec in (stats).Where(spec => spec.Kind != StatisticsSupport.StatKind.Count && seen.Add(spec.Field)))
        {
            fields.Add(spec.Field);
        }

        return fields;
    }

    private sealed class GroupState
    {
        private readonly Dictionary<string, object?> _caseValues = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StatisticsSupport.FieldAccumulator> _accumulators = new(StringComparer.Ordinal);

        public GroupState(IFeature first, IReadOnlyList<string> caseFields, IReadOnlyList<string> numericFields)
        {
            foreach (var field in caseFields)
            {
                _caseValues[field] = first.Attributes is not null && first.Attributes.Exists(field)
                    ? first.Attributes.GetOptionalValue(field)
                    : null;
            }

            foreach (var field in numericFields)
            {
                _accumulators[field] = new StatisticsSupport.FieldAccumulator();
            }
        }

        public long Frequency { get; private set; }

        public void Accumulate(IFeature feature, IReadOnlyList<string> numericFields)
        {
            Frequency++;
            foreach (var (field, value) in StatisticsSupport.ReadNumericValues(feature, numericFields))
            {
                _accumulators[field].Add(value);
            }
        }

        public Feature ToRow(IReadOnlyList<string> caseFields, IReadOnlyList<StatisticsSupport.StatSpec> stats)
        {
            var values = new List<(string, object?)>();
            foreach (var field in caseFields)
            {
                values.Add((field, _caseValues[field]));
            }

            values.Add(("FREQUENCY", Frequency));

            foreach (var spec in stats)
            {
                if (spec.Kind == StatisticsSupport.StatKind.Count)
                {
                    continue; // FREQUENCY already covers the group count.
                }

                values.Add((spec.OutputName, _accumulators[spec.Field].Resolve(spec.Kind)));
            }

            return OverlayExecutorSupport.TableRow(values);
        }
    }
}
