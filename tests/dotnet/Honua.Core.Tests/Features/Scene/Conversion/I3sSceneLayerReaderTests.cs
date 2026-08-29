// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Scene.Conversion;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.Conversion;

/// <summary>
/// Unit tests for the I3S scene-layer descriptor reader (#1268).
/// </summary>
public sealed class I3sSceneLayerReaderTests
{
    private const string SampleSceneLayerJson = """
    {
      "id": 0,
      "layerType": "3DObject",
      "name": "Sample Buildings",
      "version": "1.7",
      "spatialReference": { "wkid": 4326, "latestWkid": 4326 },
      "fullExtent": { "xmin": -122.5, "ymin": 37.7, "xmax": -122.4, "ymax": 37.8, "zmin": 0, "zmax": 120 },
      "store": { "id": "store-1", "profile": "meshpyramids" }
    }
    """;

    [UnitTest]
    public void Parse_ValidDescriptor_BindsAllMappedFields()
    {
        var layer = I3sSceneLayerReader.Parse(Encoding.UTF8.GetBytes(SampleSceneLayerJson));

        layer.Id.Should().Be(0);
        layer.LayerType.Should().Be("3DObject");
        layer.Name.Should().Be("Sample Buildings");
        layer.SpatialReference!.Wkid.Should().Be(4326);
        layer.FullExtent!.Xmin.Should().Be(-122.5);
        layer.FullExtent.Zmax.Should().Be(120.0);
        layer.Store!.Profile.Should().Be("meshpyramids");
    }

    [UnitTest]
    public void Parse_MalformedJson_ThrowsWithStableReason()
    {
        var act = () => I3sSceneLayerReader.Parse(Encoding.UTF8.GetBytes("{ not json"));

        act.Should().Throw<I3sConversionException>()
            .Which.Reason.Should().Be(I3sConversionErrorReason.MalformedSceneLayer);
    }

}
