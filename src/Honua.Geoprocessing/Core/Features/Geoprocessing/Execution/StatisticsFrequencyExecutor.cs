// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

internal sealed class StatisticsFrequencyExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
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
            foreach (var field in summaryFields)
            {
                switch (StatisticsSupport.TryReadNumeric(feature, field, out var value))
                {
                    case true:
                        _sums[field].Add(value);
                        break;
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
