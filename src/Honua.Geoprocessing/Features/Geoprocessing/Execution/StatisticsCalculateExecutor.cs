// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

internal sealed class StatisticsCalculateExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
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
            foreach (var field in fields)
            {
                switch (StatisticsSupport.TryReadNumeric(feature, field, out var value))
                {
                    case true:
                        accumulators[field].Add(value);
                        counts[field]++;
                        break;
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
