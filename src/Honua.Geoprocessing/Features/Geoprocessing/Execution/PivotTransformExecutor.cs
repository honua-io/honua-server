// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>transform.pivot</c> executor. Reshapes a long (tall) FeatureCollection into a
/// wide one: rows sharing the same <c>groupBy</c> key are collapsed into a single
/// output feature, and the distinct values of the <c>pivotField</c> column become
/// new attribute columns whose value is taken from the <c>valueField</c> column.
/// Pure managed — no native dependency. The geometry of the first row in each group
/// is carried onto the pivoted output feature.
/// </summary>
/// <remarks>
/// Bounded memory: one accumulator per group key holds the carried group-key values
/// plus the pivoted cells discovered so far, so peak memory is proportional to the
/// wide output, not the long input (which is itself bounded by
/// <see cref="GeoprocessingExecutorOptions.MaxArtifactBytes"/>). When two input rows
/// map to the same (group, pivot) cell the later value wins (last-write-wins).
/// </remarks>
internal sealed class PivotTransformExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "transform.pivot";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var groupBy = inputs.TryGet("groupBy", out var g) && !string.IsNullOrWhiteSpace(g)
            ? g!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
        var pivotField = inputs.Require("pivotField");
        var valueField = inputs.Require("valueField");
        var prefix = inputs.GetOrDefault("prefix", string.Empty);

        var groups = new Dictionary<string, PivotGroup>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attributes = feature.Attributes ?? new AttributesTable();
            var key = BuildKey(attributes, groupBy);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new PivotGroup(feature, attributes, groupBy);
                groups[key] = group;
                order.Add(key);
            }

            if (!attributes.Exists(pivotField))
            {
                continue;
            }

            var column = Convert.ToString(attributes.GetOptionalValue(pivotField), CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(column))
            {
                continue;
            }

            var value = attributes.Exists(valueField) ? attributes.GetOptionalValue(valueField) : null;
            group.Set(prefix + column, value);
        }

        var output = new List<IFeature>(groups.Count);
        foreach (var key in order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Add(groups[key].Build());
        }

        return output;
    }

    private static string BuildKey(IAttributesTable attributes, string[] groupBy)
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

            sb.Append(attributes.Exists(groupBy[i])
                ? Convert.ToString(attributes.GetOptionalValue(groupBy[i]), CultureInfo.InvariantCulture)
                : "\u0000<null>");
        }

        return sb.ToString();
    }

    private sealed class PivotGroup
    {
        private readonly NetTopologySuite.Geometries.Geometry? _geometry;
        private readonly string[] _groupBy;
        private readonly object?[] _groupValues;
        private readonly Dictionary<string, object?> _cells = new(StringComparer.Ordinal);

        public PivotGroup(IFeature first, IAttributesTable attributes, string[] groupBy)
        {
            _geometry = first.Geometry;
            _groupBy = groupBy;
            _groupValues = new object?[groupBy.Length];
            for (var i = 0; i < groupBy.Length; i++)
            {
                _groupValues[i] = attributes.Exists(groupBy[i]) ? attributes.GetOptionalValue(groupBy[i]) : null;
            }
        }

        public void Set(string column, object? value) => _cells[column] = value;

        public Feature Build()
        {
            var table = new AttributesTable();
            for (var i = 0; i < _groupBy.Length; i++)
            {
                table.Add(_groupBy[i], _groupValues[i]);
            }

            foreach (var (column, value) in _cells)
            {
                if (table.Exists(column))
                {
                    table[column] = value;
                }
                else
                {
                    table.Add(column, value);
                }
            }

            return new Feature(_geometry, table);
        }
    }
}

/// <summary>
/// <c>transform.unpivot</c> executor. The inverse reshape: a wide FeatureCollection
/// is melted into a long one. For each input feature and each column listed in
/// <c>fields</c>, one output feature is emitted carrying the <c>keep</c> columns
/// plus two new columns — <c>nameField</c> (the source column name) and
/// <c>valueField</c> (its value) — reusing the input geometry. Pure managed.
/// </summary>
internal sealed class UnpivotTransformExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "transform.unpivot";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var fields = inputs.Require("fields")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length == 0)
        {
            throw new TransformInputException("'fields' must list at least one column to unpivot");
        }

        var keep = inputs.TryGet("keep", out var k) && !string.IsNullOrWhiteSpace(k)
            ? k!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
        var nameField = inputs.GetOrDefault("nameField", "name");
        var valueField = inputs.GetOrDefault("valueField", "value");
        var dropNulls = string.Equals(inputs.GetOrDefault("dropNulls", "false"), "true", StringComparison.OrdinalIgnoreCase);

        var output = new List<IFeature>(source.Count * Math.Max(1, fields.Length));
        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attributes = feature.Attributes ?? new AttributesTable();
            foreach (var field in fields)
            {
                var value = attributes.Exists(field) ? attributes.GetOptionalValue(field) : null;
                if (dropNulls && value is null)
                {
                    continue;
                }

                var table = new AttributesTable();
                foreach (var name in keep)
                {
                    table.Add(name, attributes.Exists(name) ? attributes.GetOptionalValue(name) : null);
                }

                Upsert(table, nameField, field);
                Upsert(table, valueField, value);
                output.Add(new Feature(feature.Geometry, table));
            }
        }

        return output;
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
}
