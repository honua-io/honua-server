// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>generalization.dissolve</c> layer-aware executor (#2325). The job-executable
/// counterpart of the layer-scoped "Dissolve" op: it streams a Honua catalog layer
/// through <c>source.honua-layer</c> and dissolves (unions) features by an optional
/// attribute group, producing one feature per group with a <c>COUNT</c> of the
/// dissolved rows plus any aggregates requested through <c>outStatistics</c>. Unlike
/// <c>analytics.buffer-aggregate</c> no buffer is applied before the union. The
/// geometry union reuses the shared <see cref="OverlayExecutorSupport"/> primitive
/// (cascaded polygon union for polygonal input, dimension-preserving unary union
/// otherwise) and the statistics reuse the shared <see cref="StatisticsSupport"/>
/// accumulators.
/// </summary>
internal sealed class LayerDissolveExecutor : LayerSourcedFeatureExecutor
{
    internal const string HandledProcessId = "generalization.dissolve";
    internal const string CountAttribute = "COUNT";

    public LayerDissolveExecutor(
        IServiceScopeFactory serviceScopeFactory,
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<LayerDissolveExecutor> logger)
        : base(serviceScopeFactory, options, logger)
    {
    }

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        List<IFeature> source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var groupByFields = StatisticsSupport.ParseFieldList(inputs.GetOrDefault("groupByFields", string.Empty));
        var dissolve = ReadBool(inputs, "dissolve", defaultValue: true);
        var stats = StatisticsSupport.ParseStatistics(inputs.GetOrDefault("outStatistics", string.Empty));

        if (!dissolve)
        {
            if (stats.Count > 0)
            {
                throw new TransformInputException(
                    "'outStatistics' requires dissolve=true; per-feature output cannot carry aggregate columns.");
            }

            // No union requested: emit the input features unchanged (attributes carried through).
            var passthrough = new List<IFeature>(source.Count);
            foreach (var feature in source)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (feature.Geometry is null || feature.Geometry.IsEmpty)
                {
                    continue;
                }

                passthrough.Add(new Feature(feature.Geometry, OverlayExecutorSupport.CopyAttributes(feature)));
            }

            return passthrough;
        }

        var groups = new Dictionary<string, GroupState>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = StatisticsSupport.GroupKey(feature, groupByFields);
            if (!groups.TryGetValue(key, out var state))
            {
                state = new GroupState(feature, groupByFields, stats);
                groups[key] = state;
                order.Add(key);
            }

            state.Add(feature);
        }

        var output = new List<IFeature>(order.Count);
        foreach (var key in order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Add(groups[key].Build());
        }

        return output;
    }

    private static bool ReadBool(StepInputReader inputs, string name, bool defaultValue)
    {
        if (!inputs.TryGet(name, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => throw new TransformInputException($"'{name}' must be a boolean (true|false)"),
        };
    }

    private sealed class GroupState
    {
        private readonly IFeature _first;
        private readonly IReadOnlyList<string> _groupByFields;
        private readonly IReadOnlyList<StatisticsSupport.StatSpec> _stats;
        private readonly Dictionary<string, StatisticsSupport.FieldAccumulator> _accumulators = new(StringComparer.Ordinal);
        private readonly FeatureCollection _geometries = new();
        private long _count;

        public GroupState(
            IFeature first,
            IReadOnlyList<string> groupByFields,
            IReadOnlyList<StatisticsSupport.StatSpec> stats)
        {
            _first = first;
            _groupByFields = groupByFields;
            _stats = stats;
        }

        public void Add(IFeature feature)
        {
            _count++;
            if (feature.Geometry is not null && !feature.Geometry.IsEmpty)
            {
                _geometries.Add(feature);
            }

            foreach (var spec in _stats)
            {
                if (spec.Kind == StatisticsSupport.StatKind.Count)
                {
                    continue;
                }

                if (!_accumulators.TryGetValue(spec.Field, out var accumulator))
                {
                    accumulator = new StatisticsSupport.FieldAccumulator();
                    _accumulators[spec.Field] = accumulator;
                }

                if (StatisticsSupport.TryReadNumeric(feature, spec.Field, out var value))
                {
                    accumulator.Add(value);
                }
            }
        }

        public Feature Build()
        {
            var attributes = new AttributesTable();
            foreach (var field in _groupByFields)
            {
                object? value = _first.Attributes is not null && _first.Attributes.Exists(field)
                    ? _first.Attributes.GetOptionalValue(field)
                    : null;
                OverlayExecutorSupport.Upsert(attributes, field, value);
            }

            OverlayExecutorSupport.Upsert(attributes, CountAttribute, _count);

            foreach (var spec in _stats)
            {
                object? value = spec.Kind == StatisticsSupport.StatKind.Count
                    ? _count
                    : _accumulators.TryGetValue(spec.Field, out var accumulator)
                        ? accumulator.Resolve(spec.Kind)
                        : null;
                OverlayExecutorSupport.Upsert(attributes, spec.OutputName, value);
            }

            var geometry = OverlayExecutorSupport.UnionGeometry(_geometries);
            return new Feature(geometry, attributes);
        }
    }
}
