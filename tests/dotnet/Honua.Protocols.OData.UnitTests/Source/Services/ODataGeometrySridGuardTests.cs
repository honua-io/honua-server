// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Protocols.OData.Services;
using NSubstitute;
using Xunit;

namespace Honua.Protocols.OData.Tests.Services;

[Trait("Category", "Unit")]
[Trait("Component", "OData")]
[global::Xunit.Trait("Tier", "Fast")]
public sealed class ODataGeometrySridGuardTests
{
    private readonly ICrsRegistry _crs = Substitute.For<ICrsRegistry>();

    public ODataGeometrySridGuardTests()
    {
        _crs.IsSridSupportedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    [Theory]
    [InlineData("geo.intersects(Geometry,geography'SRID=999999;POINT(0 0)')", 999999)]
    [InlineData("geo.distance(Geometry,geography'SRID=12345;POINT(0 0)') lt 10", 12345)]
    public async Task UnknownLiteralSrid_IsReportedWithItsCode(string filter, int srid)
    {
        _crs.IsSridSupportedAsync(srid, Arg.Any<CancellationToken>()).Returns(false);

        var error = await ODataGeometrySridGuard.FindUnsupportedSridErrorAsync(_crs, [filter], CancellationToken.None);

        error.Should().Be($"Unsupported geometry literal SRID '{srid}'.");
    }

    [Fact]
    public async Task SupportedLiteralSrid_IsAccepted()
    {
        var error = await ODataGeometrySridGuard.FindUnsupportedSridErrorAsync(
            _crs, ["geo.intersects(Geometry,geography'SRID=3857;POINT(0 0)')"], CancellationToken.None);

        error.Should().BeNull();
        await _crs.Received().IsSridSupportedAsync(3857, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("population gt 10")]
    [InlineData("not a filter (")]
    public async Task FilterWithoutGeometryOrUnparsable_DoesNotConsultTheRegistry(string filter)
    {
        (await ODataGeometrySridGuard.FindUnsupportedSridErrorAsync(_crs, [filter], CancellationToken.None)).Should().BeNull();

        await _crs.DidNotReceiveWithAnyArgs().IsSridSupportedAsync(default, default);
    }
}
