// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Memory;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Tests.Features.Infrastructure.Memory;

public sealed class PooledGeometryProcessorTests
{
    [Fact]
    public void WriteWkbWithPooling_LargeGeometry_MatchesStandardWriter()
    {
        var coordinates = Enumerable.Range(0, 200)
            .Select(i => new Coordinate(i, i))
            .ToArray();
        var geometry = new LineString(coordinates);

        var pooled = PooledGeometryProcessor.WriteWkbWithPooling(geometry);
        var expected = new WKBWriter().Write(geometry);

        pooled.Should().Equal(expected);
    }

    [Fact]
    public void WriteWkbWithPooling_EmptyGeometry_ReturnsEmptyArray()
    {
        var geometry = new GeometryFactory().CreatePoint();

        var result = PooledGeometryProcessor.WriteWkbWithPooling(geometry);

        result.Should().BeEmpty();
    }
}
