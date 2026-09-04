// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Protocols.GeoServices.GPServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

public sealed class GPServerResultValueMapperTests
{
    [UnitTest]
    public void Map_FeatureLayerDataUri_EmitsEsriFeatureSet()
    {
        const string geoJson = "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"properties\":{\"name\":\"x\"},\"geometry\":{\"type\":\"Point\",\"coordinates\":[1,2]}}]}";
        var dataUri = "data:application/geo+json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(geoJson));

        var value = GPServerResultValueMapper.Map(ArtifactKind.FeatureLayer, dataUri, isLocation: true);

        value.GetProperty("features").GetArrayLength().Should().Be(1);
        value.GetProperty("features")[0].GetProperty("geometry").GetProperty("x").GetDouble().Should().Be(1);
    }

    [UnitTest]
    public void Map_HostedArtifact_EmitsUrlObject()
    {
        var value = GPServerResultValueMapper.Map(
            ArtifactKind.FeatureLayer,
            "https://example.test/result.geojson",
            isLocation: true);

        value.GetProperty("url").GetString().Should().Be("https://example.test/result.geojson");
    }
}
