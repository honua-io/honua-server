// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// A catastrophic catalog LIKE times out as <see cref="ImageServerCatalogFilterException"/>,
/// which the catalog query handler maps to HTTP 400 instead of an unhandled 500.
/// </summary>
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
