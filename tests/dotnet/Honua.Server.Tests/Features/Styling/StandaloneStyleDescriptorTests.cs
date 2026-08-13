// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Styling;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Styling;

public sealed class StandaloneStyleDescriptorTests
{
    [UnitTest]
    public void FromMapLibre_LabelBeforeConcreteLayer_PrefersConcreteGeometry()
    {
        const string style =
            """
            {
              "version": 8,
              "layers": [
                { "id": "labels", "type": "symbol" },
                { "id": "parcels", "type": "fill" }
              ]
            }
            """;

        var descriptor = StandaloneStyleDescriptor.FromMapLibre("parcels", style);

        Assert.Equal(MetadataV2GeometryType.Polygon, descriptor.GeometryType);
    }

    [UnitTest]
    public void FromMapLibre_OnlySymbolLayer_FallsBackToPointGeometry()
    {
        const string style = """{"version":8,"layers":[{"id":"places","type":"symbol"}]}""";

        var descriptor = StandaloneStyleDescriptor.FromMapLibre("places", style);

        Assert.Equal(MetadataV2GeometryType.Point, descriptor.GeometryType);
    }

    [UnitTest]
    public void FromMapLibre_MultipleLayerSources_BindsToTheSymbolizingLayersSource()
    {
        // "layer-11" is declared first but is only the label overlay; the concrete line
        // layer draws from "layer-22". Binding to declaration order would rebuild the
        // canonical document against an unrelated data layer.
        const string style =
            """
            {
              "version": 8,
              "sources": {
                "layer-11": { "type": "vector", "tiles": ["https://example.test/11/{z}/{x}/{y}"] },
                "layer-22": { "type": "vector", "tiles": ["https://example.test/22/{z}/{x}/{y}"] }
              },
              "layers": [
                { "id": "labels", "type": "symbol", "source": "layer-11" },
                { "id": "roads", "type": "line", "source": "layer-22" }
              ]
            }
            """;

        var descriptor = StandaloneStyleDescriptor.FromMapLibre("roads", style);

        Assert.Equal(22, descriptor.Id);
        Assert.Equal(MetadataV2GeometryType.LineString, descriptor.GeometryType);
    }

    [UnitTest]
    public void FromMapLibre_SingleLayerSource_StillBindsWhenTheLayerNamesNoSource()
    {
        const string style =
            """
            {
              "version": 8,
              "sources": { "layer-7": { "type": "vector" } },
              "layers": [ { "id": "roads", "type": "line" } ]
            }
            """;

        var descriptor = StandaloneStyleDescriptor.FromMapLibre("roads", style);

        Assert.Equal(7, descriptor.Id);
    }

    [UnitTest]
    public void FromMapLibre_AmbiguousLayerSources_ReportsNoBinding()
    {
        // Nothing selects between the two sources, so guessing one would silently redirect
        // the style. Report "no layer" and let the converters use their geometry-only default.
        const string style =
            """
            {
              "version": 8,
              "sources": {
                "layer-11": { "type": "vector" },
                "layer-22": { "type": "vector" }
              },
              "layers": [ { "id": "roads", "type": "line" } ]
            }
            """;

        var descriptor = StandaloneStyleDescriptor.FromMapLibre("roads", style);

        Assert.Equal(0, descriptor.Id);
        Assert.Equal(MetadataV2GeometryType.LineString, descriptor.GeometryType);
    }
}
