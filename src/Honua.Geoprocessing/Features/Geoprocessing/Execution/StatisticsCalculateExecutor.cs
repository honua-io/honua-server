// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>statistics.calculate</c> executor (#2140). Computes the documented descriptive
/// statistics (<c>COUNT</c>, <c>MIN</c>, <c>MAX</c>, <c>MEAN</c>, <c>SUM</c>,
/// <c>STDDEV</c>) for each requested field across the whole <c>input</c> dataset.
/// Emits a TABLE (null-geometry FeatureCollection) with one row per field, keyed by
/// a <c>FIELD</c> column. Null/non-numeric values are excluded from the numeric
/// aggregates; <c>STDDEV</c> is the sample (n-1) standard deviation and is null for
/// fewer than two numeric values. The <c>input</c> layer is supplied inline as a
/// <c>data:application/geo+json;base64</c> data URI. Pure managed — no GDAL/GEOS
/// native dependency.
/// </summary>
internal sealed class StatisticsCalculateExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    /// <summary>The canonical process id this executor handles.</summary>
    internal const string HandledProcessId = "statistics.calculate";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var fields = StatisticsSupport.ParseFieldList(inputs.GetOrDefault("fields", string.Empty));
        if (fields.Count == 0)
        {
            throw new TransformInputException("statistics.calculate requires at least one 'fields' field");
        }

        var accumulators = new Dictionary<string, StatisticsSupport.FieldAccumulator>(StringComparer.Ordinal);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            accumulators[field] = new StatisticsSupport.FieldAccumulator();
            counts[field] = 0;
        }

        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Not a .Where(...) candidate: TryReadNumeric's out value feeds two running
            // aggregates in the body, so filtering separately would mean parsing twice.
            foreach (var field in fields)
            {
                if (StatisticsSupport.TryReadNumeric(feature, field, out var value))
                {
                    accumulators[field].Add(value);
                    counts[field]++;
                }
            }
        }

        var output = new List<IFeature>(fields.Count);
        foreach (var field in fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var accumulator = accumulators[field];
            output.Add(OverlayExecutorSupport.TableRow(new (string, object?)[]
            {
                ("FIELD", field),
                ("COUNT", counts[field]),
                ("MIN", accumulator.Resolve(StatisticsSupport.StatKind.Min)),
                ("MAX", accumulator.Resolve(StatisticsSupport.StatKind.Max)),
                ("MEAN", accumulator.Resolve(StatisticsSupport.StatKind.Mean)),
                ("SUM", accumulator.Resolve(StatisticsSupport.StatKind.Sum)),
                ("STDDEV", accumulator.Resolve(StatisticsSupport.StatKind.StdDev)),
            }));
        }

        return output;
    }
}
