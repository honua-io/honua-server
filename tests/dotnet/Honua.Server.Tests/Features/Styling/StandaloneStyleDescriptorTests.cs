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
}
