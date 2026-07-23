// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>data-management.append</c> executor (#2139). Appends the <c>append</c> source
/// FeatureCollection INTO the schema of the <c>input</c> target, mirroring Esri's
/// <c>Append</c>: the target's features are preserved verbatim and each source
/// feature is projected onto the target's field set (only target fields are kept;
/// missing source values become null). An optional <c>fieldMap</c> of
/// <c>source:target</c> pairs (semicolon-separated) remaps source field names. The
/// target field set is the union of attribute names across the target features (or
/// the source field set when the target is empty). Distinct from
/// <see cref="OverlayMergeExecutor"/>, which produces a new union-schema output.
/// Pure managed NetTopologySuite — no GDAL/GEOS native dependency.
/// </summary>
internal sealed class DataManagementAppendExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    /// <summary>The canonical process id this executor handles.</summary>
    internal const string HandledProcessId = "data-management.append";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var appendLayer = OverlayExecutorSupport.ReadLayer(inputs, "append", _options.CurrentValue.MaxArtifactBytes);
        var fieldMap = ReadFieldMap(inputs);

        var targetFields = ResolveTargetFields(source, appendLayer);

        var output = new List<IFeature>(source.Count + appendLayer.Count);

        // Target features preserved verbatim.
        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Add(new Feature(feature.Geometry, OverlayExecutorSupport.CopyAttributes(feature)));
        }

        // Source features projected onto the target schema.
        foreach (var feature in appendLayer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projected = new AttributesTable();
            foreach (var targetField in targetFields)
            {
                projected.Add(targetField, ResolveSourceValue(feature, targetField, fieldMap));
            }

            output.Add(new Feature(feature.Geometry, projected));
        }

        return output;
    }

    private static object? ResolveSourceValue(
        IFeature feature,
        string targetField,
        IReadOnlyDictionary<string, string> fieldMap)
    {
        // fieldMap maps source->target; invert to find the source field feeding this target.
        var sourceField = targetField;
        foreach (var (src, dst) in fieldMap)
        {
            if (string.Equals(dst, targetField, StringComparison.Ordinal))
            {
                sourceField = src;
                break;
            }
        }

        if (feature.Attributes is not null && feature.Attributes.Exists(sourceField))
        {
            return feature.Attributes.GetOptionalValue(sourceField);
        }

        return null;
    }

    private static List<string> ResolveTargetFields(FeatureCollection target, FeatureCollection fallback)
    {
        var fields = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Collect(FeatureCollection features)
        {
            foreach (var feature in features)
            {
                if (feature.Attributes is null)
                {
                    continue;
                }

                // Not a .Where(...) candidate: seen.Add(name) is the dedup side effect
                // itself, so a filter predicate here would double as the mutation.
                foreach (var name in (feature.Attributes.GetNames()).Where(name => seen.Add(name)))
                {
                    fields.Add(name);
                }
            }
        }

        Collect(target);
        if (fields.Count == 0)
        {
            Collect(fallback);
        }

        return fields;
    }

    private static Dictionary<string, string> ReadFieldMap(StepInputReader inputs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!inputs.TryGet("fieldMap", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return map;
        }

        foreach (var token in raw!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                throw new TransformInputException(
                    $"'fieldMap' entry '{token}' is not a 'source:target' pair");
            }

            map[parts[0]] = parts[1];
        }

        return map;
    }
}
