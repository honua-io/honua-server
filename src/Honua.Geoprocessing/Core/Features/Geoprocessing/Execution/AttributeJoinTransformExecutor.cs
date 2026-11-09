// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

internal sealed class AttributeJoinTransformExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "transform.attribute-join";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var leftKeys = SplitRequired(inputs, "leftKeys");
        var rightKeys = inputs.TryGet("rightKeys", out var rk) && !string.IsNullOrWhiteSpace(rk)
            ? Split(rk!)
            : leftKeys;

        if (leftKeys.Length != rightKeys.Length)
        {
            throw new TransformInputException(
                $"leftKeys ({leftKeys.Length}) and rightKeys ({rightKeys.Length}) must have the same number of columns");
        }

        var joinType = inputs.GetOrDefault("type", "inner").Trim().ToLowerInvariant() switch
        {
            "inner" => JoinType.Inner,
            "left" => JoinType.Left,
            var other => throw new TransformInputException(
                $"join type '{other}' is not supported (allowed: inner, left)"),
        };

        var carryFields = inputs.TryGet("fields", out var f) && !string.IsNullOrWhiteSpace(f)
            ? Split(f!)
            : Array.Empty<string>();
        var prefix = inputs.GetOrDefault("prefix", string.Empty);

        var rightUri = inputs.Require("right");
        if (!FeatureCollectionArtifact.TryParseDataUri(rightUri, out var rightFeatures, out var rightError, _options.CurrentValue.MaxArtifactBytes))
        {
            throw new TransformInputException($"'right' {rightError}");
        }

        var buildSide = BuildHashTable(rightFeatures, rightKeys, cancellationToken);

        var output = new List<IFeature>(source.Count);
        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attributes = feature.Attributes;
            if (!TryBuildKey(attributes, leftKeys, out var key))
            {
                // A null/missing key never matches; emit unmatched for left joins.
                if (joinType == JoinType.Left)
                {
                    output.Add(Project(feature, null, carryFields, prefix));
                }

                continue;
            }

            if (buildSide.TryGetValue(key, out var matches))
            {
                foreach (var match in matches)
                {
                    output.Add(Project(feature, match, carryFields, prefix));
                }
            }
            else if (joinType == JoinType.Left)
            {
                output.Add(Project(feature, null, carryFields, prefix));
            }
        }

        return output;
    }

    private static Dictionary<string, List<IFeature>> BuildHashTable(
        FeatureCollection rightFeatures,
        string[] rightKeys,
        CancellationToken cancellationToken)
    {
        var table = new Dictionary<string, List<IFeature>>(StringComparer.Ordinal);
        foreach (var feature in rightFeatures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryBuildKey(feature.Attributes, rightKeys, out var key))
            {
                continue;
            }

            if (!table.TryGetValue(key, out var bucket))
            {
                bucket = new List<IFeature>(1);
                table[key] = bucket;
            }

            bucket.Add(feature);
        }

        return table;
    }

    private static Feature Project(
        IFeature left,
        IFeature? right,
        string[] carryFields,
        string prefix)
    {
        var merged = new AttributesTable();
        if (left.Attributes is not null)
        {
            foreach (var name in left.Attributes.GetNames())
            {
                merged.Add(name, left.Attributes.GetOptionalValue(name));
            }
        }

        // Carry the requested right-side fields. When no explicit field list is
        // given, bring every right attribute that does not already collide.
        var names = carryFields.Length > 0
            ? carryFields
            : right?.Attributes?.GetNames() ?? Array.Empty<string>();

        foreach (var name in names)
        {
            var outputName = prefix.Length > 0 ? prefix + name : name;
            object? value = right?.Attributes is not null && right.Attributes.Exists(name)
                ? right.Attributes.GetOptionalValue(name)
                : null;

            if (merged.Exists(outputName))
            {
                merged[outputName] = value;
            }
            else
            {
                merged.Add(outputName, value);
            }
        }

        return new Feature(left.Geometry, merged);
    }

    private static bool TryBuildKey(IAttributesTable? attributes, string[] keys, out string key)
    {
        key = string.Empty;
        if (attributes is null)
        {
            return false;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < keys.Length; i++)
        {
            if (!attributes.Exists(keys[i]))
            {
                return false;
            }

            var value = attributes.GetOptionalValue(keys[i]);
            if (value is null)
            {
                return false;
            }

            if (i > 0)
            {
                // U+001F (unit separator) is the inter-column delimiter. Column values are
                // escaped via EscapeKeyComponent before appending, so the single unescaped
                // U+001F here is always unambiguously a column boundary.
                sb.Append('\u001F');
            }

            sb.Append(EscapeKeyComponent(Convert.ToString(value, CultureInfo.InvariantCulture)));
        }

        key = sb.ToString();
        return true;
    }

    // U+001E and U+001F inside a component are escaped so a single unescaped U+001F always means a column boundary.
    private static string? EscapeKeyComponent(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value.IndexOfAny(['\u001E', '\u001F']) < 0)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 4);
        foreach (var ch in value)
        {
            if (ch is '\u001E' or '\u001F')
            {
                sb.Append('\u001E'); // Escape prefix before any structural character.
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string[] SplitRequired(StepInputReader inputs, string name)
    {
        var parts = Split(inputs.Require(name));
        if (parts.Length == 0)
        {
            throw new TransformInputException($"'{name}' must list at least one key column");
        }

        return parts;
    }

    private static string[] Split(string raw)
        => raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private enum JoinType
    {
        Inner,
        Left,
    }
}
