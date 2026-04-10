// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OgcMaps;

[Collection("Database")]
[Protocol(Protocols.OgcApiMaps)]
public sealed class OgcMapsTemporalMosaicTests
{
    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    [Operation(Operations.Render)]
    public async Task GetCollectionMap_WithDatetimeOnCommunityEdition_ReturnsForbidden()
    {
        var fixture = await CreateFixtureAsync(HonuaEdition.Community).ConfigureAwait(false);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/ogc/maps/collections/{WebAppFixture.TestLayerId}/map" +
                "?bbox=0,0,4,2&width=64&height=32&f=png&datetime=2024-02-15T00:00:00Z");

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    [Operation(Operations.Render)]
    public async Task GetCollectionMap_WithDatetimeOnProEdition_RendersTemporalRasterMosaic()
    {
        var renderer = new StubRasterMapRenderer();
        var fixture = await CreateFixtureAsync(HonuaEdition.Pro, renderer).ConfigureAwait(false);
        try
        {
            var expectedTimestamp = DateTimeOffset.Parse("2024-02-15T00:00:00Z", CultureInfo.InvariantCulture);
            var response = await fixture.Client.GetAsync(
                $"/ogc/maps/collections/{WebAppFixture.TestLayerId}/map" +
                "?bbox=0,0,4,2&width=64&height=32&f=png&datetime=2024-02-15T00:00:00Z");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
            (await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Should().NotBeEmpty();
            renderer.LastCollectionRequest.Should().NotBeNull();
            renderer.LastCollectionRequest!.Value.DateTime.Should().Be(expectedTimestamp);
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<WebAppFixture> CreateFixtureAsync(
        HonuaEdition edition,
        IRasterMapRenderer? renderer = null)
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ILicenseStatusProvider>(new StubLicenseStatusProvider(edition));
        if (renderer != null)
        {
            fixture.ReplaceService(renderer);
        }

        await fixture.InitializeAsync().ConfigureAwait(false);
        await RasterIntegrationTestData.SeedIssue522MosaicAsync(fixture).ConfigureAwait(false);
        return fixture;
    }

    private sealed class StubLicenseStatusProvider(HonuaEdition edition) : ILicenseStatusProvider
    {
        public LicenseStatus GetCurrentStatus()
            => new(edition, IsValid: true, ExpiresAt: null, LicensedTo: "test");

        public Task<LicenseUploadResult> UploadLicenseAsync(
            Stream licenseStream,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new LicenseUploadResult(false, "Stub does not support upload."));
    }

    private sealed class StubRasterMapRenderer : IRasterMapRenderer
    {
        public MapRenderRequest? LastCollectionRequest { get; private set; }

        public Task<RasterResult> RenderCollectionMapAsync(
            int layerId,
            MapRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            LastCollectionRequest = request;
            return Task.FromResult(new RasterResult
            {
                Data = [0x89, 0x50, 0x4E, 0x47],
                ContentType = "image/png",
                Width = request.Width,
                Height = request.Height,
                Srid = request.Crs ?? request.BoundingBoxCrs ?? 4326
            });
        }

        public Task<RasterResult> RenderDatasetMapAsync(
            int[] layerIds,
            MapRenderRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RasterResult> RenderStyledMapAsync(
            int layerId,
            string styleId,
            MapRenderRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
