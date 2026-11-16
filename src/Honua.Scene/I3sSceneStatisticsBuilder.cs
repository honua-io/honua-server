// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Scene;

public static class I3sSceneStatisticsBuilder
{
    public const string ObjectIdFieldKey = "f_0";

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
        var featureIndex = 0;
        while (featureIndex < features.Count)
        {
            if (features[featureIndex].Attributes.TryGetValue(attributeKey, out var value) &&
                value is not null)
            {
                present++;
                distinct.Add(value as string ?? value.ToString() ?? string.Empty);
            }

            featureIndex++;
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

        long count = pages.Sum(page => page.Nodes.Count(node => node.Mesh is not null));

        return count;
    }
}
