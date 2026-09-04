// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Protocols.Cog;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Cog;

/// <summary>Exercises the registration-to-ImageServer binding through HTTP and the real COG store.</summary>
[Collection("Database")]
[Protocol(TestProtocols.Cog, TestProtocols.ImageServer)]
public sealed class CogRegistrationTileTests
{
    [IntegrationTest]
    [Operation(Operations.CogAdmin, Operations.GetTile)]
    [Endpoint("POST /api/v1/admin/cloud-rasters")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task RegisteredCog_DistinctStorageAndPublicationIds_ServesOnlyBoundService()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("resource-a", "imagery-a", MetadataV2ResourceType.RasterDataset)
            .AddStorageBinding("binding-a", "resource-a", "rasters:42", storageLayerId: 42)
            .AddService("service-a", "imagery-a", protocols: [ServiceProtocols.ImageServer])
            .AddPublication("publication-a", "service-a", "resource-a", layerIndex: 1,
                storageBindingId: "binding-a", publicationType: MetadataV2PublicationType.EsriImageLayer)
            .AddResource("resource-b", "imagery-b", MetadataV2ResourceType.RasterDataset)
            .AddStorageBinding("binding-b", "resource-b", "rasters:1", storageLayerId: 1)
            .AddService("service-b", "imagery-b", protocols: [ServiceProtocols.ImageServer])
            .AddPublication("publication-b", "service-b", "resource-b", layerIndex: 2,
                storageBindingId: "binding-b", publicationType: MetadataV2PublicationType.EsriImageLayer)
            .BuildProvider();
        var tile = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var rangeReader = Substitute.For<ICloudRangeReader>();
        rangeReader.Provider.Returns(CloudStorageProvider.AwsS3);
        rangeReader.GetObjectMetadataAsync("bucket", "bound.tif", Arg.Any<CancellationToken>())
            .Returns(new CloudObjectMetadata { SizeBytes = 1024, ETag = "etag-1" });
        rangeReader.ReadRangeAsync("bucket", "bound.tif", 16, tile.Length, Arg.Any<CancellationToken>()).Returns(tile);
        rangeReader.ReadRangeAsync("bucket", "bound.tif", 16, tile.Length, "etag-1", Arg.Any<CancellationToken>()).Returns(tile);
        var metadataReader = Substitute.For<ICogMetadataReader>();
        metadataReader.ReadMetadataAsync(Arg.Any<ICloudRangeReader>(), "bucket", "bound.tif", Arg.Any<CancellationToken>())
            .Returns(new CogMetadata(256, 256, 3, "uint8", 3857, "JPEG", 256, 256,
                [new CogOverviewLevel(0, 256, 256, 8, [16L], [tile.Length])],
                new RasterExtent
                {
                    XMin = -20037508.342789244,
                    YMin = -20037508.342789244,
                    XMax = 20037508.342789244,
                    YMax = 20037508.342789244,
                    Srid = 3857
                }));
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.QueryRastersAsync(default, default!, default).ReturnsForAnyArgs(Array.Empty<RasterInfo>());
        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IMetadataV2GraphProvider>();
            services.RemoveAll<IMetadataV2GraphStore>();
            services.AddSingleton<IMetadataV2GraphProvider>(graph);
            services.AddSingleton<IMetadataV2GraphStore>(graph);
            services.AddSingleton(rasterStore);
            // Isolate binding behavior from license provisioning; use the real resolver/store.
            services.AddScoped<ICogTileResolver>(provider => new CogTileResolver([rangeReader], metadataReader,
                provider.GetRequiredService<ICogStore>(), provider.GetRequiredService<IMemoryCache>(),
                NullLogger<CogTileResolver>.Instance));
        });
        await fixture.InitializeAsync();
        try
        {
            using var registration = await fixture.Client.PostAsJsonAsync("/api/v1/admin/cloud-rasters", new
            {
                layerId = 1,
                name = "bound-cog",
                provider = "AwsS3",
                bucket = "bucket",
                objectKey = "bound.tif"
            });
            registration.StatusCode.Should().Be(HttpStatusCode.Created);

            using var ownTile = await fixture.Client.GetAsync("/rest/services/imagery-a/ImageServer/tile/0/0/0?format=jpg");
            ownTile.StatusCode.Should().Be(HttpStatusCode.OK);
            ownTile.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
            (await ownTile.Content.ReadAsByteArrayAsync()).Should().Equal(tile);

            using var otherTile = await fixture.Client.GetAsync("/rest/services/imagery-b/ImageServer/tile/0/0/0?format=jpg");
            await otherTile.AssertGeoServicesErrorAsync(404);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
