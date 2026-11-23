// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Query;
using Honua.Core.Queries.Filters;
using Honua.Protocols.OData.Models;
using Honua.Protocols.OData.Services;
using NSubstitute;
using Xunit;

namespace Honua.Protocols.OData.Tests.Services;

[Trait("Category", "Unit")]
[Trait("Component", "OData")]
[global::Xunit.Trait("Tier", "Fast")]
public sealed class ODataQueryServiceSridTests
{
    private readonly ICrsRegistry _crs = Substitute.For<ICrsRegistry>();

    public ODataQueryServiceSridTests()
    {
        _crs.IsSridSupportedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    [Theory]
    [InlineData("geo.intersects(Geometry,geography'SRID=999999;POINT(0 0)')", 999999)]
    [InlineData("geo.distance(Geometry,geography'SRID=12345;POINT(0 0)') lt 10", 12345)]
    public async Task UnknownLiteralSrid_IsReportedWithItsCode(string filter, int srid)
    {
        _crs.IsSridSupportedAsync(srid, Arg.Any<CancellationToken>()).Returns(false);

        var error = await Create().FindUnsupportedFilterSridAsync(filter, _crs, CancellationToken.None);

        error.Should().Be($"Unsupported explicit geometry CRS 'EPSG:{srid}'.");
    }

    [Fact]
    public async Task SupportedLiteralSrid_IsAccepted()
    {
        var error = await Create().FindUnsupportedFilterSridAsync(
            "geo.intersects(Geometry,geography'SRID=3857;POINT(0 0)')", _crs, CancellationToken.None);

        error.Should().BeNull();
        await _crs.Received().IsSridSupportedAsync(3857, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("population gt 10")]
    [InlineData("not a filter (")]
    public async Task FilterWithoutGeometryOrUnparsable_DoesNotConsultTheRegistry(string filter)
    {
        (await Create().FindUnsupportedFilterSridAsync(filter, _crs, CancellationToken.None)).Should().BeNull();

        await _crs.DidNotReceiveWithAnyArgs().IsSridSupportedAsync(default, default);
    }

    private ODataQueryService Create() => new(
        new FilterExpressionService(Substitute.For<IFilterExpressionTranslator>()),
        Substitute.For<IQueryParameterAdapter<ODataQueryParameters>>(),
        Substitute.For<IQueryProcessor>());
}
