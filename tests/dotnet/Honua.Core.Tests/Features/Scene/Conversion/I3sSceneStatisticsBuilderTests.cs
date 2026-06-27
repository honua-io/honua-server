// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;
using Honua.Scene;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.Conversion;

/// <summary>
/// Unit tests for the feature-driven I3S statistics builder (#1811): the
/// <c>statistics/f_{n}/0</c> summary an ArcGIS client reads to drive identify,
/// classification, and renderer ranges. The summary must report real
/// min/max/count derived from the features' projected attributes.
/// </summary>
public sealed class I3sSceneStatisticsBuilderTests
{
    [UnitTest]
    public void Build_ObjectIdField_ReportsFeatureIdRange()
    {
        var features = Features();

        var stats = I3sSceneStatisticsBuilder.Build(features, I3sAttributeSchemaBuilder.BuildObjectIdField());

        stats.Stats.TotalValuesCount.Should().Be(3);
        stats.Stats.Min.Should().Be(11);
        stats.Stats.Max.Should().Be(33);
        stats.Stats.Count.Should().Be(3);
    }

    [UnitTest]
    public void Build_NumericField_ReportsMinMaxOverPresentValues()
    {
        var features = Features();
        var field = new I3sAttributeStorageInfo
        {
            Key = "f_1",
            Name = "HEIGHT",
            AttributeValues = new I3sAttributeValues { ValueType = I3sAttributeBufferBuilder.Float64ValueType },
        };

        var stats = I3sSceneStatisticsBuilder.Build(features, field);

        // Only two of three features carry HEIGHT (12.5 and 7.0).
        stats.Stats.TotalValuesCount.Should().Be(2);
        stats.Stats.Min.Should().Be(7.0);
        stats.Stats.Max.Should().Be(12.5);
        stats.Stats.Count.Should().Be(2);
    }

    [UnitTest]
    public void Build_StringField_ReportsPresenceAndDistinctCountWithoutRange()
    {
        var features = Features();
        var field = new I3sAttributeStorageInfo
        {
            Key = "f_2",
            Name = "NAME",
            AttributeValues = new I3sAttributeValues { ValueType = I3sAttributeBufferBuilder.StringValueType, Encoding = "UTF-8" },
        };

        var stats = I3sSceneStatisticsBuilder.Build(features, field);

        stats.Stats.TotalValuesCount.Should().Be(2);
        stats.Stats.Count.Should().Be(2);
        stats.Stats.Min.Should().BeNull();
        stats.Stats.Max.Should().BeNull();
    }

    private static IReadOnlyList<SceneFeature> Features() =>
    [
        FeatureWith(11, new Dictionary<string, object?> { ["HEIGHT"] = 12.5, ["NAME"] = "alpha" }),
        FeatureWith(22, new Dictionary<string, object?> { ["HEIGHT"] = 7.0, ["NAME"] = "beta" }),
        FeatureWith(33, new Dictionary<string, object?>()),
    ];

    private static SceneFeature FeatureWith(long id, IReadOnlyDictionary<string, object?> attributes) => new()
    {
        Id = id,
        Geometry = new SceneFeatureGeometry
        {
            Kind = SceneGeometryKind.Polygon,
            Vertices = new[]
            {
                new SceneVertex(-122.42, 37.77, 0.0),
                new SceneVertex(-122.41, 37.77, 0.0),
                new SceneVertex(-122.41, 37.78, 0.0),
            },
        },
        Attributes = attributes,
    };
}
