// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Scene.Generation;

namespace Honua.Core.Tests.Features.Scene.Generation;

public sealed class GeodesicErrorTests
{
    [Fact]
    public void RootGeometricError_AntimeridianSpan_UsesTheShortArc()
    {
        // 20° of longitude at the equator on the WGS 84 prime vertical, about 2.226e6 m.
        var error = GeodesicError.RootGeometricError(170d, 0d, -170d, 0d, 0d, 0d);
        error.Should().BeApproximately(2_226_389.816d, 1d);
    }

    [Fact]
    public void RootGeometricError_EquatorLatitudeSpan_UsesMeridionalRadius()
    {
        // 1° of latitude at the equator is about 110574 m, not a * 1°.
        var error = GeodesicError.RootGeometricError(0d, 0d, 0d, 1d, 0d, 0d);
        error.Should().BeApproximately(110_574.276d, 1d);
    }
}
