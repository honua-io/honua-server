// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

public sealed class ImageServerCatalogLikeTimeoutTests
{
    [UnitTest]
    public void Apply_PathologicalLike_ThrowsFilterException()
    {
        var items = new[]
        {
            new ImageServerCatalogItem
            {
                ObjectId = 1,
                Name = new string('a', 10_000),
                PixelType = "8BUI",
            },
        };

        var act = () => new ImageServerCatalogFilterEvaluator()
            .Apply(items, "Name LIKE '%a%a%a%a%a%a%a%a%a%a%a%a%b'")
            .ToList();

        act.Should().Throw<ImageServerCatalogFilterException>();
    }
}
