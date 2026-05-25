// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Services.Connectors;
using Honua.TestKit.Attributes;
using NetTopologySuite.Features;

namespace Honua.Core.Tests.Features.GeoETL;

/// <summary>
/// Unit coverage for the managed inline-GeoJSON layer feature source that resolves
/// a layer-scope GP input reference into a streamed feature collection.
/// </summary>
public sealed class InlineGeoJsonLayerFeatureSourceTests
{
    private const string FeatureCollection =
        "{\"type\":\"FeatureCollection\",\"features\":[" +
        "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[1,2]}," +
        "\"properties\":{\"name\":\"a\"}}," +
        "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[3,4]}," +
        "\"properties\":{\"name\":\"b\"}}]}";

    [UnitTest]
    public void CanResolve_OnlyInlineGeoJson()
    {
        var source = new InlineGeoJsonLayerFeatureSource();

        source.CanResolve(LayerFeatureSourceKind.InlineGeoJson).Should().BeTrue();
        source.CanResolve(LayerFeatureSourceKind.CatalogLayer).Should().BeFalse();
        source.CanResolve(LayerFeatureSourceKind.QueryResult).Should().BeFalse();
    }

    [UnitTest]
    public async Task ReadAsync_StreamsEachFeatureWithAttributes()
    {
        var source = new InlineGeoJsonLayerFeatureSource();
        var reference = new LayerFeatureReference
        {
            Kind = LayerFeatureSourceKind.InlineGeoJson,
            InlineGeoJson = FeatureCollection
        };

        var features = new List<IFeature>();
        await foreach (var feature in source.ReadAsync(reference))
        {
            features.Add(feature);
        }

        features.Should().HaveCount(2);
        features[0].Attributes!["name"].Should().Be("a");
        features[1].Attributes!["name"].Should().Be("b");
    }

    [UnitTest]
    public async Task ReadAsync_RejectsNonInlineReference()
    {
        var source = new InlineGeoJsonLayerFeatureSource();
        var reference = new LayerFeatureReference
        {
            Kind = LayerFeatureSourceKind.CatalogLayer,
            LayerId = "100"
        };

        var act = async () =>
        {
            await foreach (var _ in source.ReadAsync(reference))
            {
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTest]
    public async Task ReadAsync_RejectsEmptyInlineDocument()
    {
        var source = new InlineGeoJsonLayerFeatureSource();
        var reference = new LayerFeatureReference
        {
            Kind = LayerFeatureSourceKind.InlineGeoJson,
            InlineGeoJson = "   "
        };

        var act = async () =>
        {
            await foreach (var _ in source.ReadAsync(reference))
            {
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
