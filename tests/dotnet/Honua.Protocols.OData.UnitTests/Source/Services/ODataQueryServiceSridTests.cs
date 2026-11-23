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
    public async Task EnsureFilterSridsSupported_UnknownLiteralSrid_IsRejected(string filter, int srid)
    {
        _crs.IsSridSupportedAsync(srid, Arg.Any<CancellationToken>()).Returns(false);

        var act = () => Create().EnsureFilterSridsSupportedAsync(filter, CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage($"*EPSG:{srid}*");
    }

    [Fact]
    public async Task EnsureFilterSridsSupported_SupportedLiteralSrid_IsAccepted()
    {
        await Create().EnsureFilterSridsSupportedAsync(
            "geo.intersects(Geometry,geography'SRID=3857;POINT(0 0)')", CancellationToken.None);

        await _crs.Received().IsSridSupportedAsync(3857, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindUnsupportedSridError_FilterWithoutGeometry_DoesNotConsultTheRegistry()
    {
        var parsed = new FilterExpressionService(Substitute.For<IFilterExpressionTranslator>())
            .Parse(FilterLanguage.OData, "population gt 10");

        (await Create().FindUnsupportedSridErrorAsync(parsed.Expression, CancellationToken.None)).Should().BeNull();
        await _crs.DidNotReceiveWithAnyArgs().IsSridSupportedAsync(default, default);
    }

    private ODataQueryService Create() => new(
        new FilterExpressionService(Substitute.For<IFilterExpressionTranslator>()),
        Substitute.For<IQueryParameterAdapter<ODataQueryParameters>>(),
        Substitute.For<IQueryProcessor>(),
        _crs);
}
