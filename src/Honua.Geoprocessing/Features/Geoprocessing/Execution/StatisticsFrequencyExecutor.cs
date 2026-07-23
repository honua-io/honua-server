// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>statistics.frequency</c> executor (#2140). Computes how often each distinct
/// combination of <c>frequencyFields</c> occurs, matching Esri's <c>Frequency</c>.
/// Emits a TABLE (null-geometry FeatureCollection) with one row per distinct
/// combination carrying the field values, a <c>FREQUENCY</c> count, and an optional
/// <c>SUM_&lt;field&gt;</c> for every field in <c>summaryFields</c>. A null value is
/// treated as its own distinct combination component. The <c>input</c> layer is
/// supplied inline as a <c>data:application/geo+json;base64</c> data URI. Pure
/// managed — no GDAL/GEOS native dependency.
/// </summary>
internal sealed class StatisticsFrequencyExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    /// <summary>The canonical process id this executor handles.</summary>
    internal const string HandledProcessId = "statistics.frequency";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var frequencyFields = StatisticsSupport.ParseFieldList(inputs.GetOrDefault("frequencyFields", string.Empty));
        if (frequencyFields.Count == 0)
        {
            throw new TransformInputException("statistics.frequency requires at least one 'frequencyFields' field");
        }

        var summaryFields = StatisticsSupport.ParseFieldList(inputs.GetOrDefault("summaryFields", string.Empty));

        var order = new List<string>();
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);

        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = StatisticsSupport.GroupKey(feature, frequencyFields);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new Group(feature, frequencyFields, summaryFields);
                groups.Add(key, group);
                order.Add(key);
            }

            group.Accumulate(feature, summaryFields);
        }

        var output = new List<IFeature>(order.Count);
        foreach (var key in order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Add(groups[key].ToRow(frequencyFields, summaryFields));
        }

        return output;
    }

    private sealed class Group
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StatisticsSupport.FieldAccumulator> _sums = new(StringComparer.Ordinal);

        public Group(IFeature first, IReadOnlyList<string> frequencyFields, IReadOnlyList<string> summaryFields)
        {
            foreach (var field in frequencyFields)
            {
                _values[field] = first.Attributes is not null && first.Attributes.Exists(field)
                    ? first.Attributes.GetOptionalValue(field)
                    : null;
            }

            foreach (var field in summaryFields)
            {
                _sums[field] = new StatisticsSupport.FieldAccumulator();
            }
        }

        public long Frequency { get; private set; }

        public void Accumulate(IFeature feature, IReadOnlyList<string> summaryFields)
        {
            Frequency++;
            // Not a .Where(...) candidate: TryReadNumeric's out value is the addend, so
            // filtering separately would mean parsing each value twice.
            // codeql[cs/linq/missed-where] -- predicate binds state or awaits; retain imperative control flow.
            foreach (var field in summaryFields)
            {
                if (StatisticsSupport.TryReadNumeric(feature, field, out var value))
                {
                    _sums[field].Add(value);
                }
            }
        }

        public Feature ToRow(IReadOnlyList<string> frequencyFields, IReadOnlyList<string> summaryFields)
        {
            var values = new List<(string, object?)>();
            foreach (var field in frequencyFields)
            {
                values.Add((field, _values[field]));
            }

            values.Add(("FREQUENCY", Frequency));

            foreach (var field in summaryFields)
            {
                values.Add(("SUM_" + field, _sums[field].Resolve(StatisticsSupport.StatKind.Sum)));
            }

            return OverlayExecutorSupport.TableRow(values);
        }
    }
}
