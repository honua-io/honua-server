// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wcs20;

[Collection("Database")]
[Protocol(TestProtocols.Wcs201)]
public sealed class Wcs20SubsetTransformFailureTests : IAsyncLifetime
{
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly WebAppFixture _fixture;

    public Wcs20SubsetTransformFailureTests()
    {
        var transforms = Substitute.For<ICoordinateTransformService>();
        // The shared transform service signals unavailable transforms with
        // null (extent) or false (batch points); neither proves disjointness.
        _rasterStore.GetPrimaryRasterInfoAsync(WebAppFixture.TestLayerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterInfo?>(new RasterInfo
            {
                Id = 377,
                LayerId = WebAppFixture.TestLayerId,
                Name = "transform-failure",
                Width = 4,
                Height = 4,
                BandCount = 1,
                PixelType = "32BF",
                Srid = 4326,
                Extent = new RasterExtent { XMin = 0, YMin = 0, XMax = 1, YMax = 1, Srid = 4326 }
            }));
        _fixture = new WebAppFixture().ReplaceService(_rasterStore).ReplaceService(transforms);
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    public async Task Wcs_GetCoverage_UnavailableSubsetTransform_PreservesServerError()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wcs?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=coverage_{WebAppFixture.TestLayerId}&SUBSETTINGCRS=EPSG:3857&SUBSET=x(1000,2000)&SUBSET=y(1000,2000)");
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, content);
        content.Should().Contain("exceptionCode=\"NoApplicableCode\"");
        content.Should().NotContain("InvalidSubsetting");
        await _rasterStore.DidNotReceive().ExportImageAsync(
            Arg.Any<int>(), Arg.Any<long>(), Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>());
    }
}
