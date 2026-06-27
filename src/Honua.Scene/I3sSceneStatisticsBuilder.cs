// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Scene;

/// <summary>
/// Builds the I3S per-field statistics summary served at
/// <c>statistics/f_{n}/0</c> (#1811). The summary lets an ArcGIS client drive
/// identify, classification, and renderer ranges for one attribute field.
/// </summary>
/// <remarks>
/// The statistics are derived honestly from what the hosted scene knows without
/// decoding tile content: the count of content-bearing tiles in the projected
/// node tree gives the number of addressable node geometries (a defensible lower
/// bound on distinct <c>OBJECTID</c> values for identify discovery). Per-value
/// min/max for arbitrary user attribute fields requires decoding the glTF
/// <c>EXT_structural_metadata</c> property tables — that decode is the deferred
/// hard-lane work (#1810/#1811), so this builder emits the field-presence /
/// count summary the descriptor can back today rather than fabricating ranges.
/// </remarks>
public static class I3sSceneStatisticsBuilder
{
    /// <summary>
    /// The descriptor's default key for the synthetic <c>OBJECTID</c> field every
    /// served 3D Object node carries.
    /// </summary>
    public const string ObjectIdFieldKey = "f_0";

    /// <summary>
    /// Builds the statistics summary for one attribute-storage field of a hosted
    /// scene.
    /// </summary>
    /// <param name="scene">The hosted scene dataset.</param>
    /// <param name="field">The attribute-storage descriptor for the field.</param>
    /// <returns>The statistics document for the field.</returns>
    public static I3sAttributeStatisticsDocument Build(
        SceneDataset scene,
        I3sAttributeStorageInfo field)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(field);

        var nodeGeometryCount = CountContentNodes(scene);

        // The OBJECTID key is the per-node feature key: every addressable node
        // geometry contributes at least one value, so the node-geometry count is
        // the honest totalValuesCount the layer can back without a tile decode.
        // Other (user) fields share the same addressing but their value ranges
        // need the deferred property-table decode, so only the count is emitted.
        return new I3sAttributeStatisticsDocument
        {
            Stats = new I3sAttributeStatistics
            {
                TotalValuesCount = nodeGeometryCount,
                Count = string.Equals(field.Key, ObjectIdFieldKey, StringComparison.Ordinal)
                    ? nodeGeometryCount
                    : null,
            },
        };
    }

    /// <summary>
    /// Builds the statistics summary for one attribute-storage field directly
    /// from the ordered scene features, computing real numeric ranges instead of
    /// the node-count lower bound.
    /// </summary>
    /// <remarks>
    /// For the synthetic <c>OBJECTID</c> field and any <c>Float64</c> user field
    /// the summary carries the true <c>totalValuesCount</c> (features with a
    /// non-null value), <c>min</c>/<c>max</c>, and distinct-value <c>count</c>
    /// derived from the features' projected attributes. A <c>String</c> field
    /// has no numeric range, so only <c>totalValuesCount</c> and the distinct
    /// <c>count</c> are emitted.
    /// </remarks>
    /// <param name="features">The ordered scene features supplying attribute values.</param>
    /// <param name="field">The attribute-storage descriptor for the field.</param>
    /// <returns>The statistics document for the field.</returns>
    public static I3sAttributeStatisticsDocument Build(
        IReadOnlyList<SceneFeature> features,
        I3sAttributeStorageInfo field)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(field);

        if (string.Equals(field.Key, ObjectIdFieldKey, StringComparison.Ordinal))
        {
            return BuildNumericStats(
                features.Select(static f => (double)f.Id),
                features.Count);
        }

        var attributeKey = field.Name;
        if (string.IsNullOrEmpty(attributeKey))
        {
            return new I3sAttributeStatisticsDocument();
        }

        if (string.Equals(field.AttributeValues?.ValueType, I3sAttributeBufferBuilder.Float64ValueType, StringComparison.Ordinal))
        {
            var numerics = features
                .Select(f => I3sAttributeBufferBuilder.TryGetNumeric(f, attributeKey))
                .Where(static v => v.HasValue)
                .Select(static v => v!.Value);
            return BuildNumericStats(numerics, presentCountHint: null);
        }

        // Non-numeric (string) field: emit value presence + distinct count only.
        var present = 0;
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in features)
        {
            if (feature.Attributes.TryGetValue(attributeKey, out var value) && value is not null)
            {
                present++;
                distinct.Add(value as string ?? value.ToString() ?? string.Empty);
            }
        }

        return new I3sAttributeStatisticsDocument
        {
            Stats = new I3sAttributeStatistics
            {
                TotalValuesCount = present,
                Count = distinct.Count,
            },
        };
    }

    private static I3sAttributeStatisticsDocument BuildNumericStats(IEnumerable<double> values, int? presentCountHint)
    {
        long total = 0;
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        var distinct = new HashSet<double>();

        foreach (var value in values)
        {
            total++;
            if (value < min)
            {
                min = value;
            }

            if (value > max)
            {
                max = value;
            }

            distinct.Add(value);
        }

        if (total == 0)
        {
            return new I3sAttributeStatisticsDocument
            {
                Stats = new I3sAttributeStatistics { TotalValuesCount = presentCountHint ?? 0 },
            };
        }

        return new I3sAttributeStatisticsDocument
        {
            Stats = new I3sAttributeStatistics
            {
                TotalValuesCount = presentCountHint ?? total,
                Min = min,
                Max = max,
                Count = distinct.Count,
            },
        };
    }

    private static long CountContentNodes(SceneDataset scene)
    {
        if (!I3sNodeStore.TryBuildNodePages(scene, out var pages))
        {
            return 0;
        }

        long count = 0;
        foreach (var page in pages)
        {
            foreach (var node in page.Nodes)
            {
                if (node.Mesh is not null)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
