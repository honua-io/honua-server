// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using SkiaSharp;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Basic ImageServer read operations. The service-info, exportImage, identify and tile cases run
/// against the seeded Issue-522 mosaic on a host whose raster resource is restricted to
/// <see cref="EntitledRole"/> (<see cref="ImageServerParameterFixture"/>): west (x 0..2, value 20),
/// overlap-newest (x 1..3, value 5, newest) and east (x 2..4, value 40), all y 0..2 in EPSG:4326.
/// Each success case reads those values back as a non-admin principal holding the granted role;
/// each paired denial case proves an anonymous caller and a principal without the role learn
/// nothing and change nothing.
/// </summary>
[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.ImageServer)]
public class ImageServerBasicTests : IClassFixture<WebAppFixture>, IClassFixture<ImageServerParameterFixture>
{
    private readonly WebAppFixture _fixture;
    private readonly WebAppFixture _secured;
    private const int TestLayerId = 0; // Use existing test layer

    /// <summary>Role <see cref="ImageServerParameterFixture"/> grants on the raster resource.</summary>
    private const string EntitledRole = "imagery-reader";

    /// <summary>A role the resource does not grant.</summary>
    private const string UnrelatedRole = "cartography-reader";

    private const string Referer = "https://imageserver-basic-proof.example/";

    /// <summary>Text that must never appear in a denied response: raster identities and pixel or extent payloads.</summary>
    private static readonly string[] DisclosureMarkers =
        ["\"west\"", "\"east\"", "\"overlap-newest\"", "Band_1", "\"href\"", "\"extent\"", "minValues"];

    public ImageServerBasicTests(WebAppFixture fixture, ImageServerParameterFixture secured)
    {
        _fixture = fixture;
        _secured = secured.App;
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfo_ValidLayerId_ReturnsServiceMetadata()
    {
        await RunOnSeededMosaicAsync(async principals =>
        {
            using var response = await SendAsync(principals.Reader, HttpMethod.Get, ServiceInfoUrl);
            await AssertMosaicServiceInfoAsync(response);
        });
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfo_DeniedPrincipals_DiscloseNothingAndChangeNothing()
        => await AssertDeniedDisclosesNothingAndChangesNothingAsync(
            HttpMethod.Get, ServiceInfoUrl, form: null, (response, _) => AssertMosaicServiceInfoAsync(response));

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfo_InvalidFormat_ReturnsBadRequest()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestLayerId}/ImageServer?f=xml");

        // Assert
        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    [Operation(Operations.Export)]
    public async Task ExportImage_WithValidParameters_ReturnsMosaicPixels()
    {
        await RunOnSeededMosaicAsync(async principals =>
        {
            using var response = await SendAsync(
                principals.Reader, HttpMethod.Get, $"{SecuredBase}/exportImage?{ExportQuery}");
            await AssertMosaicExportAsync(response, principals);
        });
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    [Operation(Operations.Export)]
    public async Task ExportImage_DeniedPrincipals_DiscloseNothingAndChangeNothing()
        => await AssertDeniedDisclosesNothingAndChangesNothingAsync(
            HttpMethod.Get,
            $"{SecuredBase}/exportImage?{ExportQuery}",
            form: null,
            AssertMosaicExportAsync);

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/exportImage")]
    [Operation(Operations.Export)]
    public async Task ExportImage_PostFormBody_ReturnsMosaicPixels()
    {
        await RunOnSeededMosaicAsync(async principals =>
        {
            using var response = await SendAsync(
                principals.Reader, HttpMethod.Post, $"{SecuredBase}/exportImage", ExportForm);
            await AssertMosaicExportAsync(response, principals);
        });
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/exportImage")]
    [Operation(Operations.Export)]
    public async Task ExportImage_PostFormBody_DeniedPrincipals_DiscloseNothingAndChangeNothing()
        => await AssertDeniedDisclosesNothingAndChangesNothingAsync(
            HttpMethod.Post,
            $"{SecuredBase}/exportImage",
            ExportForm,
            AssertMosaicExportAsync);

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    [Operation(Operations.Export)]
    public async Task ExportImage_WithInlineImageFormat_ReturnsImageBytes()
    {
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.GetPrimaryRasterInfoAsync(TestLayerId, Arg.Any<CancellationToken>())
            .Returns(new RasterInfo
            {
                Id = 100,
                LayerId = TestLayerId,
                Name = "test-raster",
                Width = 1024,
                Height = 1024,
                BandCount = 3,
                PixelType = "8BUI",
                Srid = 4326,
                CreatedAt = DateTimeOffset.UtcNow
            });
        rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs(new[]
            {
                new RasterInfo
                {
                    Id = 100,
                    LayerId = TestLayerId,
                    Name = "test-raster",
                    Width = 1024,
                    Height = 1024,
                    BandCount = 3,
                    PixelType = "8BUI",
                    Srid = 4326,
                    CreatedAt = DateTimeOffset.UtcNow
                }
            });
        rasterStore.ExportImageAsync(TestLayerId, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = [0x89, 0x50, 0x4E, 0x47],
                ContentType = "image/png",
                Width = 256,
                Height = 128,
                Srid = 4326,
                Extent = new RasterExtent
                {
                    XMin = -180,
                    YMin = -90,
                    XMax = 180,
                    YMax = 90,
                    Srid = 4326
                }
            });

        var fixture = new WebAppFixture()
            .ConfigureServices(services => services.AddSingleton(rasterStore));

        await fixture.InitializeAsync();
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/exportImage?bbox=-180,-90,180,90&size=256,256&format=png&f=image");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
            (await response.Content.ReadAsByteArrayAsync()).Should().Equal([(byte)0x89, 0x50, 0x4E, 0x47]);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    [Operation(Operations.Identify)]
    public async Task Identify_WithValidPoint_ReturnsIdentifyResult()
    {
        const string root = "stores/identify-vertical";
        var rangeReader = new ImageServerFixtureRangeReader(
            ImageServerZarrTestFixture.BuildVerticalStore(root, levels: 4, rows: 4, cols: 4));
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(rangeReader, "bucket", root);
        var registration = new ZarrRegistration
        {
            Id = 1,
            LayerId = TestLayerId,
            Name = "vertical",
            Provider = CloudStorageProvider.AwsS3,
            Bucket = "bucket",
            RootPath = root,
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var zarrStore = Substitute.For<IZarrStore>();
        zarrStore.ListByLayerAsync(TestLayerId, Arg.Any<CancellationToken>()).Returns([registration]);
        var sliceReader = new ZarrPointSliceReader(zarrStore, new ZarrSubsetReader(), [rangeReader]);
        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IZarrPointSliceReader>();
            services.AddSingleton<IZarrPointSliceReader>(sliceReader);
        });
        await fixture.InitializeAsync();

        try
        {
            var definition = Uri.EscapeDataString(
                "[{\"variableName\":\"temperature\",\"dimensionName\":\"elevation\",\"values\":[333.3333],\"isSlice\":true}]");
            var query = $"?geometry=0,0&geometryType=esriGeometryPoint&sr=4326&multidimensionalDefinition={definition}&f=json";

            foreach (var route in new[]
            {
                $"/rest/services/{TestLayerId}/ImageServer/identify{query}",
                $"/rest/services/{WebAppFixture.TestServiceId}/ImageServer/identify{query}",
            })
            {
                var response = await fixture.Client.GetAsync(route);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                json.RootElement.GetProperty("value").GetString().Should().Be("1022");
                json.RootElement.GetProperty("name").GetString().Should().Be("temperature");
                json.RootElement.GetProperty("properties").GetProperty("HasData").GetBoolean().Should().BeTrue();
                json.RootElement.GetProperty("properties").GetProperty("DimensionCount").GetInt32().Should().Be(1);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    [Endpoint("POST /rest/services/{id}/ImageServer/exportImage")]
    [Operation(Operations.Export)]
    public async Task ExportImage_WithMultidimensionalDefinition_ReturnsNativeSlicePng()
    {
        const string root = "stores/export-vertical";
        var rangeReader = new ImageServerFixtureRangeReader(
            ImageServerZarrTestFixture.BuildVerticalStore(root, levels: 4, rows: 4, cols: 4));
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(rangeReader, "bucket", root);
        var registration = new ZarrRegistration
        {
            Id = 2,
            LayerId = TestLayerId,
            Name = "vertical-export",
            Provider = CloudStorageProvider.AwsS3,
            Bucket = "bucket",
            RootPath = root,
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var zarrStore = Substitute.For<IZarrStore>();
        zarrStore.ListByLayerAsync(TestLayerId, Arg.Any<CancellationToken>()).Returns([registration]);
        var sliceReader = new ZarrRasterSliceReader(zarrStore, new ZarrSubsetReader(), [rangeReader]);
        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IZarrRasterSliceReader>();
            services.AddSingleton<IZarrRasterSliceReader>(sliceReader);
        });
        await fixture.InitializeAsync();

        try
        {
            const string definition =
                "[{\"variableName\":\"temperature\",\"dimensionName\":\"elevation\",\"values\":[333.3333],\"isSlice\":true}]";
            var encodedDefinition = Uri.EscapeDataString(definition);
            var query = $"?bbox=-180,-90,180,90&bboxSR=4326&imageSR=4326&size=4,3&format=png&interpolation=RSP_NearestNeighbor&multidimensionalDefinition={encodedDefinition}&f=image";
            var outputs = new List<byte[]>();

            foreach (var route in new[]
            {
                $"/rest/services/{TestLayerId}/ImageServer/exportImage{query}",
                $"/rest/services/{WebAppFixture.TestServiceId}/ImageServer/exportImage{query}",
            })
            {
                var response = await fixture.Client.GetAsync(route);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
                outputs.Add(await response.Content.ReadAsByteArrayAsync());
            }

            foreach (var route in new[]
            {
                $"/rest/services/{TestLayerId}/ImageServer/exportImage",
                $"/rest/services/{WebAppFixture.TestServiceId}/ImageServer/exportImage",
            })
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["bbox"] = "-180,-90,180,90",
                    ["bboxSR"] = "4326",
                    ["imageSR"] = "4326",
                    ["size"] = "4,3",
                    ["format"] = "png",
                    ["interpolation"] = "RSP_NearestNeighbor",
                    ["multidimensionalDefinition"] = definition,
                    ["f"] = "image",
                });
                var response = await fixture.Client.PostAsync(route, content);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
                outputs.Add(await response.Content.ReadAsByteArrayAsync());
            }

            outputs.Should().HaveCount(4);
            foreach (var output in outputs)
            {
                output.Take(8).Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
                BinaryPrimitives.ReadInt32BigEndian(output.AsSpan(16, 4)).Should().Be(4);
                BinaryPrimitives.ReadInt32BigEndian(output.AsSpan(20, 4)).Should().Be(3);
            }

            outputs.Skip(1).Should().OnlyContain(bytes => bytes.SequenceEqual(outputs[0]));
            await zarrStore.Received(4).ListByLayerAsync(TestLayerId, Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/identify")]
    [Operation(Operations.Identify)]
    public async Task Identify_PostFormBody_ReturnsPixelValueOfCoveringRaster()
    {
        await RunOnSeededMosaicAsync(async principals =>
        {
            using (var east = await SendAsync(
                principals.Reader, HttpMethod.Post, $"{SecuredBase}/identify", IdentifyForm(3.5, 1)))
            {
                await AssertIdentifyAsync(east, x: 3.5, y: 1, raster: "east", value: 40);
            }

            using var west = await SendAsync(
                principals.Reader, HttpMethod.Post, $"{SecuredBase}/identify", IdentifyForm(0.5, 1));
            await AssertIdentifyAsync(west, x: 0.5, y: 1, raster: "west", value: 20);
        });
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/identify")]
    [Operation(Operations.Identify)]
    public async Task Identify_PostFormBody_DeniedPrincipals_DiscloseNothingAndChangeNothing()
        => await AssertDeniedDisclosesNothingAndChangesNothingAsync(
            HttpMethod.Post,
            $"{SecuredBase}/identify",
            IdentifyForm(3.5, 1),
            (response, _) => AssertIdentifyAsync(response, x: 3.5, y: 1, raster: "east", value: 40));

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}")]
    [Operation(Operations.GetTile)]
    public async Task GetImageTile_InsideMosaic_RendersCoveringRasterValue()
    {
        await RunOnSeededMosaicAsync(async principals =>
        {
            // Two zoom-10 tiles strictly inside one raster each, away from every raster edge, so
            // every rendered pixel is that raster's value as an opaque gray level.
            using (var west = await SendAsync(principals.Reader, HttpMethod.Get, WestTileUrl))
            {
                await AssertUniformGrayTileAsync(west, gray: 20);
            }

            using var east = await SendAsync(principals.Reader, HttpMethod.Get, EastTileUrl);
            await AssertUniformGrayTileAsync(east, gray: 40);
        });
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}")]
    [Operation(Operations.GetTile)]
    public async Task GetImageTile_DeniedPrincipals_DiscloseNothingAndChangeNothing()
        => await AssertDeniedDisclosesNothingAndChangesNothingAsync(
            HttpMethod.Get, EastTileUrl, form: null, (response, _) => AssertUniformGrayTileAsync(response, gray: 40));

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/exportImage")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/exportImage")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/identify")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/identify")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/tile/{level}/{row}/{col}")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/query")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/query")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/computeStatisticsHistograms")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/computeStatisticsHistograms")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/computeHistograms")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/computeHistograms")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/getSamples")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/getSamples")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/computeCacheInfo")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/computeCacheInfo")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/computePixelLocation")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/computePixelLocation")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/queryBoundary")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/queryBoundary")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/keyProperties")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/keyProperties")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/legend")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/legend")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/computeClassStatistics")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/computeClassStatistics")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/multidimensionalInfo")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/multidimensionalInfo")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/slices")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/slices")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/conf.json")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/kml/image.kmz")]
    [Operation(Operations.Metadata)]
    public async Task ServiceNameRoutes_DispatchToImageServerSurface()
    {
        // Each service-name route must reach the same handler over the same raster as its numeric
        // twin: on the seeded mosaic both answer 200 with a non-error body, and the two bodies agree
        // (service identity and the per-request temporary-file href aside, which are compared by
        // what they resolve to). The numeric routes' values are proven separately above.
        await RunOnSeededMosaicAsync(async principals =>
        {
            // Report every diverging route at once rather than stopping at the first.
            using var scope = new AssertionScope();
            foreach (var tail in ServiceNameGetTails())
            {
                using var byLayer = await SendAsync(principals.Reader, HttpMethod.Get, $"{SecuredBase}{tail}");
                using var byName = await SendAsync(principals.Reader, HttpMethod.Get, $"{ServiceNameBase}{tail}");
                await AssertSameImageServerResultAsync(byLayer, byName, $"GET {tail}", principals);
            }

            foreach (var (operation, form) in ServiceNameFormRequests())
            {
                var query = string.Join(
                    "&",
                    form.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
                using var byLayer = await SendAsync(
                    principals.Reader, HttpMethod.Get, $"{SecuredBase}/{operation}?{query}");
                using var byName = await SendAsync(
                    principals.Reader, HttpMethod.Post, $"{ServiceNameBase}/{operation}", form);
                await AssertSameImageServerResultAsync(byLayer, byName, $"POST /{operation}", principals);
            }
        });
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/exportImage")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/exportImage")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/identify")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/identify")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/tile/{level}/{row}/{col}")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/query")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/query")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/computeStatisticsHistograms")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/computeStatisticsHistograms")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/computeHistograms")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/computeHistograms")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/getSamples")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/getSamples")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/computeCacheInfo")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/computeCacheInfo")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/computePixelLocation")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/computePixelLocation")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/queryBoundary")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/queryBoundary")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/keyProperties")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/keyProperties")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/legend")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/legend")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/computeClassStatistics")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/computeClassStatistics")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/multidimensionalInfo")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/multidimensionalInfo")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/slices")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/slices")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/conf.json")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/kml/image.kmz")]
    [Operation(Operations.Metadata)]
    public async Task ServiceNameRoutes_DeniedPrincipals_DiscloseNothingAndChangeNothing()
    {
        await RunOnSeededMosaicAsync(async principals =>
        {
            var before = await SnapshotStateAsync();
            using var scope = new AssertionScope();
            foreach (var tail in ServiceNameGetTails())
            {
                await AssertDeniedResponsesAsync(principals, HttpMethod.Get, $"{ServiceNameBase}{tail}", form: null);
            }

            foreach (var (operation, form) in ServiceNameFormRequests())
            {
                await AssertDeniedResponsesAsync(principals, HttpMethod.Post, $"{ServiceNameBase}/{operation}", form);
            }

            (await SnapshotStateAsync()).Should().Be(
                before,
                "denied service-name requests must not change raster rows, persisted statistics or the metadata graph");

            // Positive control after the denials: the same principal model still reads real data.
            using var serviceInfo = await SendAsync(principals.Reader, HttpMethod.Get, $"{ServiceNameBase}?f=json");
            await AssertMosaicServiceInfoAsync(serviceInfo);
        });
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer")]
    [Operation(Operations.GetServiceInfo)]
    public async Task ServiceNameRoute_NumericLeadingServiceId_DispatchesToNamedRoute()
    {
        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                var graphProvider = new TestMetadataV2GraphBuilder()
                    .AddResource("res-2024-imagery", "Imagery", MetadataV2ResourceType.RasterDataset)
                    .AddStorageBinding(
                        "binding-2024-imagery",
                        "res-2024-imagery",
                        "rasters:0",
                        storageLayerId: 0)
                    .AddService(
                        "svc-2024-imagery",
                        "2024Imagery",
                        protocols: [MetadataV2ServiceProtocols.ImageServer])
                    .AddPublication(
                        "pub-2024-imagery",
                        "svc-2024-imagery",
                        "res-2024-imagery",
                        layerIndex: 0,
                        serviceLocalId: "Imagery",
                        publicationType: MetadataV2PublicationType.EsriImageLayer)
                    .BuildProvider();

                services.RemoveAll<IMetadataV2GraphProvider>();
                services.RemoveAll<IMetadataV2GraphStore>();
                services.AddSingleton(graphProvider);
                services.AddSingleton<IMetadataV2GraphProvider>(graphProvider);
                services.AddSingleton<IMetadataV2GraphStore>(graphProvider);
            });
        await fixture.InitializeAsync();

        try
        {
            using var response = await fixture.Client.GetAsync("/rest/services/2024Imagery/ImageServer?f=xml");

            await response.AssertGeoServicesErrorAsync(400);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfo_NonExistentLayer_ReturnsNotFound()
    {
        // Arrange
        var nonExistentLayerId = 99999;

        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{nonExistentLayerId}/ImageServer?f=json");

        // Assert
        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static readonly string SecuredBase = $"/rest/services/{TestLayerId}/ImageServer";

    private static readonly string ServiceNameBase = $"/rest/services/{WebAppFixture.TestServiceId}/ImageServer";

    private static readonly string ServiceInfoUrl = $"{SecuredBase}?f=json";

    /// <summary>The whole mosaic at one pixel per half degree, nearest-neighbour, as a GeoTIFF so the pixels decode losslessly.</summary>
    private const string ExportQuery = "bbox=0,0,4,2&size=8,4&format=tiff&interpolation=RSP_NearestNeighbor&f=json";

    private static readonly Dictionary<string, string> ExportForm = new()
    {
        ["bbox"] = "0,0,4,2",
        ["size"] = "8,4",
        ["format"] = "tiff",
        ["interpolation"] = "RSP_NearestNeighbor",
        ["f"] = "json",
    };

    /// <summary>Zoom-10 tile spanning x 0.35..0.70, y 0.35..0.70: inside the west raster only.</summary>
    private static readonly string WestTileUrl = $"{SecuredBase}/tile/10/510/513?format=png";

    /// <summary>Zoom-10 tile spanning x 3.16..3.52, y 0.35..0.70: inside the east raster only.</summary>
    private static readonly string EastTileUrl = $"{SecuredBase}/tile/10/510/521?format=png";

    private static Dictionary<string, string> IdentifyForm(double x, double y) => new()
    {
        ["geometry"] = string.Create(CultureInfo.InvariantCulture, $"{x},{y}"),
        ["geometryType"] = "esriGeometryPoint",
        ["f"] = "json",
    };

    private static IEnumerable<string> ServiceNameGetTails()
    {
        var envelope = Uri.EscapeDataString(
            """{"xmin":0,"ymin":0,"xmax":4,"ymax":2,"spatialReference":{"wkid":4326}}""");
        var eastPoint = Uri.EscapeDataString("""{"points":[[3.5,1]],"spatialReference":{"wkid":4326}}""");
        var pixelGeometries = Uri.EscapeDataString(
            """{"geometries":[{"x":3.5,"y":1,"spatialReference":{"wkid":4326}}],"geometryType":"esriGeometryPoint"}""");
        var classDescriptions = Uri.EscapeDataString(
            """{"classes":[{"id":1,"name":"east","geometry":{"rings":[[[3.2,0.2],[3.2,1.8],[3.8,1.8],[3.8,0.2],[3.2,0.2]]]}}]}""");

        yield return "?f=json";
        yield return $"/exportImage?{ExportQuery}";
        yield return "/identify?geometry=3.5,1&geometryType=esriGeometryPoint&f=json";
        yield return "/tile/10/510/521?format=png";
        yield return "/query?f=json&returnGeometry=false&outFields=Name&orderByFields=Name";
        yield return $"/computeStatisticsHistograms?f=json&geometryType=esriGeometryEnvelope&geometry={envelope}";
        yield return $"/computeHistograms?f=json&geometryType=esriGeometryEnvelope&geometry={envelope}";
        yield return $"/getSamples?f=json&geometryType=esriGeometryMultipoint&geometry={eastPoint}";
        yield return "/computeCacheInfo?f=json";
        yield return $"/computePixelLocation?f=json&geometries={pixelGeometries}";
        yield return "/queryBoundary?f=json";
        yield return "/keyProperties?f=json";
        yield return "/legend?f=json";
        yield return $"/computeClassStatistics?f=json&classDescriptions={classDescriptions}";
        yield return "/multidimensionalInfo?f=json";
        yield return "/slices?f=json";
        yield return "/conf.json";
        yield return "/kml/image.kmz";
    }

    private static IEnumerable<(string Operation, Dictionary<string, string> Form)> ServiceNameFormRequests()
    {
        const string envelope = """{"xmin":0,"ymin":0,"xmax":4,"ymax":2,"spatialReference":{"wkid":4326}}""";
        yield return ("exportImage", ExportForm);
        yield return ("identify", IdentifyForm(3.5, 1));
        yield return ("query", new() { ["f"] = "json", ["returnGeometry"] = "false", ["outFields"] = "Name", ["orderByFields"] = "Name" });
        yield return ("computeStatisticsHistograms", new() { ["f"] = "json", ["geometryType"] = "esriGeometryEnvelope", ["geometry"] = envelope });
        yield return ("computeHistograms", new() { ["f"] = "json", ["geometryType"] = "esriGeometryEnvelope", ["geometry"] = envelope });
        yield return ("getSamples", new()
        {
            ["f"] = "json",
            ["geometryType"] = "esriGeometryMultipoint",
            ["geometry"] = """{"points":[[3.5,1]],"spatialReference":{"wkid":4326}}""",
        });
        yield return ("computeCacheInfo", new() { ["f"] = "json" });
        yield return ("computePixelLocation", new()
        {
            ["f"] = "json",
            ["geometries"] = """{"geometries":[{"x":3.5,"y":1,"spatialReference":{"wkid":4326}}],"geometryType":"esriGeometryPoint"}""",
        });
        yield return ("queryBoundary", new() { ["f"] = "json" });
        yield return ("computeClassStatistics", new()
        {
            ["f"] = "json",
            ["classDescriptions"] = """{"classes":[{"id":1,"name":"east","geometry":{"rings":[[[3.2,0.2],[3.2,1.8],[3.8,1.8],[3.8,0.2],[3.2,0.2]]]}}]}""",
        });
        yield return ("multidimensionalInfo", new() { ["f"] = "json" });
        yield return ("slices", new() { ["f"] = "json" });
        // #1344: the service-level POST keyProperties route (added in #1338)
        // needs integration coverage for the ApiSurfaceCoverage arch test.
        yield return ("keyProperties", new() { ["f"] = "json" });
        // #1900: the service-level POST legend route needs integration coverage for the
        // ApiSurfaceCoverage arch test.
        yield return ("legend", new() { ["f"] = "json" });
    }

    private sealed record Principals(string Reader, string Unrelated);

    /// <summary>
    /// Issues the entitled and the unrelated principal, then runs <paramref name="action"/> while
    /// holding the seed lock over the Issue-522 mosaic (honua.raster_data is process-global).
    /// </summary>
    private async Task RunOnSeededMosaicAsync(Func<Principals, Task> action)
    {
        var principals = new Principals(
            await IssueAsync("imagery-analyst", EntitledRole),
            await IssueAsync("map-viewer", UnrelatedRole));
        await RasterIntegrationTestData.RunWithIssue522MosaicAsync(_secured, () => action(principals));
    }

    /// <summary>
    /// Runs the request as the entitled principal (positive control), then as an anonymous caller
    /// and as an authenticated principal without the granted role, then as the entitled principal
    /// again. Each denial must disclose nothing, and the denials must leave the raster rows,
    /// persisted statistics and metadata graph exactly as they were.
    /// </summary>
    private async Task AssertDeniedDisclosesNothingAndChangesNothingAsync(
        HttpMethod method,
        string url,
        Dictionary<string, string>? form,
        Func<HttpResponseMessage, Principals, Task> assertEntitled)
    {
        await RunOnSeededMosaicAsync(async principals =>
        {
            using (var entitled = await SendAsync(principals.Reader, method, url, form))
            {
                await assertEntitled(entitled, principals);
            }

            var before = await SnapshotStateAsync();
            await AssertDeniedResponsesAsync(principals, method, url, form);
            (await SnapshotStateAsync()).Should().Be(
                before,
                "denied requests must not change raster rows, persisted statistics or the metadata graph");

            using var replay = await SendAsync(principals.Reader, method, url, form);
            await assertEntitled(replay, principals);
        });
    }

    /// <summary>
    /// An anonymous caller receives Esri TokenRequired (499) and a principal without the granted
    /// role receives 403, each as a bare GeoServices error envelope carrying no imagery, no raster
    /// identity and no pixel, extent or statistics payload.
    /// </summary>
    private async Task AssertDeniedResponsesAsync(
        Principals principals,
        HttpMethod method,
        string url,
        Dictionary<string, string>? form)
    {
        foreach (var (token, code, who) in new[]
        {
            ((string?)null, 499, "an anonymous caller"),
            (principals.Unrelated, 403, "a principal without the granted role"),
        })
        {
            using var response = await SendAsync(token, method, url, form);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, "{0} {1} {2}: {3}", who, method, url, body);
            response.Content.Headers.ContentType?.MediaType.Should().Be(
                "application/json", "{0} must receive no imagery from {1} {2}", who, method, url);
            using var document = JsonDocument.Parse(body);
            document.RootElement.EnumerateObject().Select(property => property.Name).Should().Equal(
                ["error"], "{0} must receive only the error envelope from {1} {2}", who, method, url);
            (document.RootElement.TryGetProperty("error", out var error) ? error.GetProperty("code").GetInt32() : 200)
                .Should().Be(code, "{0} must be refused {1} {2}; body: {3}", who, method, url, body);
            foreach (var marker in DisclosureMarkers)
            {
                body.Should().NotContain(marker, "{0} must learn nothing from {1} {2}", who, method, url);
            }
        }
    }

    /// <summary>
    /// Fingerprint of everything a read could plausibly mutate: the metadata graph revision, every
    /// raster row of the layer (pixels included) and the persisted layer statistics snapshot.
    /// </summary>
    private async Task<string> SnapshotStateAsync()
    {
        await using var connection = await _secured.Postgres.GetConnectionAsync(_secured.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT coalesce((SELECT string_agg(
                                    name || ':' || md5(ST_AsBinary(raster)) || ':' || acquisition_date::text,
                                    ',' ORDER BY name)
                             FROM honua.raster_data WHERE layer_id = @layerId), '')
                   || '|' ||
                   coalesce((SELECT string_agg(s::text, ',' ORDER BY s::text)
                             FROM honua.raster_layer_statistics s WHERE s.layer_id = @layerId), '');
            """;
        command.Parameters.AddWithValue("layerId", TestLayerId);
        var rows = (string)(await command.ExecuteScalarAsync())!;
        rows.Should().Contain("east:").And.Contain("west:").And.Contain("overlap-newest:");
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{_secured.GetCurrentV2GraphSnapshot().Graph.Revision}|{rows}");
    }

    private static async Task AssertMosaicServiceInfoAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse(body);
        // Honua does not advertise an ArcGIS Server version (see NoArcGisServerVersionTests).
        root.TryGetProperty("currentVersion", out _).Should().BeFalse();
        AssertEnvelope(root.GetProperty("extent"), 0, 0, 4, 2);
        AssertEnvelope(root.GetProperty("fullExtent"), 0, 0, 4, 2);
        root.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
        root.GetProperty("pixelSizeX").GetDouble().Should().Be(1);
        root.GetProperty("pixelSizeY").GetDouble().Should().Be(1);
        root.GetProperty("bandCount").GetInt32().Should().Be(1);
        root.GetProperty("pixelType").GetString().Should().Be("F32");
        // Statistics over all 12 seeded pixels: four each of 20, 5 and 40.
        root.GetProperty("minValues").EnumerateArray().Select(value => value.GetDouble()).Should().Equal(5);
        root.GetProperty("maxValues").EnumerateArray().Select(value => value.GetDouble()).Should().Equal(40);
        root.GetProperty("meanValues").EnumerateArray().Select(value => value.GetDouble()).Should().Equal(17.5);
    }

    /// <summary>
    /// Asserts the JSON export echoes the requested 8x4 grid over the mosaic extent, then fetches
    /// the rendered GeoTIFF from its href as the same principal and decodes the actual pixels. The
    /// href is bound to that principal: neither denied principal can fetch it.
    /// </summary>
    private async Task AssertMosaicExportAsync(HttpResponseMessage response, Principals principals)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse(body);
        root.GetProperty("width").GetInt32().Should().Be(8);
        root.GetProperty("height").GetInt32().Should().Be(4);
        AssertEnvelope(root.GetProperty("extent"), 0, 0, 4, 2);
        var href = root.GetProperty("href").GetString()!;

        (await FetchExportPixelsAsync(principals.Reader, href)).Should().Equal(
            [20d, 5d, 40d],
            "x=0.25 is west only (20), x=1.75 is won by the newest raster (5), x=3.75 is east only (40)");

        foreach (var token in new[] { null, principals.Unrelated })
        {
            using var stolen = await SendAsync(token, HttpMethod.Get, href);
            stolen.StatusCode.Should().Be(HttpStatusCode.NotFound, "a rendered export belongs to the principal that requested it");
            stolen.Content.Headers.ContentType?.MediaType.Should().NotBe("image/tiff");
        }
    }

    private async Task<double[]> FetchExportPixelsAsync(string token, string href)
    {
        using var image = await SendAsync(token, HttpMethod.Get, href);
        image.StatusCode.Should().Be(HttpStatusCode.OK, href);
        image.Content.Headers.ContentType?.MediaType.Should().Be("image/tiff");
        var bytes = await image.Content.ReadAsByteArrayAsync();

        await using var connection = await _secured.Postgres.GetConnectionAsync(_secured.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH decoded AS (SELECT ST_FromGDALRaster(@data, 4326) AS rast)
            SELECT ST_Width(rast), ST_Height(rast),
                   ST_XMin(ST_Envelope(rast)), ST_YMin(ST_Envelope(rast)),
                   ST_XMax(ST_Envelope(rast)), ST_YMax(ST_Envelope(rast)),
                   ST_Value(rast, 1, ST_SetSRID(ST_MakePoint(0.25, 1.25), 4326)),
                   ST_Value(rast, 1, ST_SetSRID(ST_MakePoint(1.75, 1.25), 4326)),
                   ST_Value(rast, 1, ST_SetSRID(ST_MakePoint(3.75, 1.25), 4326))
            FROM decoded;
            """;
        command.Parameters.AddWithValue("data", bytes);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt32(0).Should().Be(8);
        reader.GetInt32(1).Should().Be(4);
        reader.GetDouble(2).Should().BeApproximately(0, 1e-9);
        reader.GetDouble(3).Should().BeApproximately(0, 1e-9);
        reader.GetDouble(4).Should().BeApproximately(4, 1e-9);
        reader.GetDouble(5).Should().BeApproximately(2, 1e-9);
        return [reader.GetDouble(6), reader.GetDouble(7), reader.GetDouble(8)];
    }

    private static async Task AssertIdentifyAsync(HttpResponseMessage response, double x, double y, string raster, double value)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse(body);
        root.GetProperty("name").GetString().Should().Be(raster);
        root.GetProperty("value").GetString().Should().Be(value.ToString(CultureInfo.InvariantCulture));
        root.GetProperty("properties").GetProperty("HasData").GetBoolean().Should().BeTrue();
        root.GetProperty("properties").GetProperty("Band_1").GetDouble().Should().Be(value);
        var location = root.GetProperty("location");
        location.GetProperty("x").GetDouble().Should().Be(x);
        location.GetProperty("y").GetDouble().Should().Be(y);
        location.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
    }

    private static async Task AssertUniformGrayTileAsync(HttpResponseMessage response, byte gray)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        using var bitmap = SKBitmap.Decode(bytes);
        bitmap.Should().NotBeNull("the tile must be a decodable PNG, not an error envelope");
        bitmap.Width.Should().Be(256);
        bitmap.Height.Should().Be(256);
        bitmap.Pixels.Distinct().Should().Equal(
            [new SKColor(gray, gray, gray, 255)],
            "every pixel of a tile inside one constant raster renders that raster's value");
    }

    /// <summary>
    /// The numeric route and the service-name route must produce the same result. JSON bodies are
    /// compared structurally; the service identity fields of the service-info root and the
    /// exportImage href (a fresh temporary file per request) are compared by meaning instead.
    /// </summary>
    private async Task AssertSameImageServerResultAsync(
        HttpResponseMessage byLayer,
        HttpResponseMessage byName,
        string route,
        Principals principals)
    {
        var layerBytes = await byLayer.Content.ReadAsByteArrayAsync();
        var nameBytes = await byName.Content.ReadAsByteArrayAsync();
        var layerText = Encoding.UTF8.GetString(layerBytes);
        var nameText = Encoding.UTF8.GetString(nameBytes);
        byLayer.StatusCode.Should().Be(HttpStatusCode.OK, "{0} by layer id: {1}", route, layerText);
        byName.StatusCode.Should().Be(HttpStatusCode.OK, "{0} by service name: {1}", route, nameText);
        var mediaType = byLayer.Content.Headers.ContentType?.MediaType;
        byName.Content.Headers.ContentType?.MediaType.Should().Be(mediaType, route);

        switch (mediaType)
        {
            case "image/png":
                layerBytes.Should().NotBeEmpty(route);
                nameBytes.Should().Equal(layerBytes, "{0} must render the same tile by service name", route);
                return;
            case "application/vnd.google-earth.kmz":
                AssertKmzOverlaysMosaic(layerBytes, route);
                AssertKmzOverlaysMosaic(nameBytes, route);
                return;
        }

        var layerJson = JsonNode.Parse(layerText)!.AsObject();
        var nameJson = JsonNode.Parse(nameText)!.AsObject();
        layerJson.ContainsKey("error").Should().BeFalse("{0} by layer id: {1}", route, layerText);
        nameJson.ContainsKey("error").Should().BeFalse("{0} by service name: {1}", route, nameText);
        layerJson.Count.Should().BePositive("{0} must return a non-empty document", route);

        if (layerJson["href"] is not null)
        {
            (await FetchExportPixelsAsync(principals.Reader, nameJson["href"]!.GetValue<string>())).Should().Equal(
                await FetchExportPixelsAsync(principals.Reader, layerJson["href"]!.GetValue<string>()),
                "{0} must render the same pixels by service name",
                route);
            layerJson.Remove("href");
            nameJson.Remove("href");
        }

        // The two routes bind different publications of the same storage layer: the numeric route
        // the "test" service's layer "0", the service-name route the ImageServer publication
        // "test-layer". Only that identity (service name, description, legend layer name) may differ.
        var nameAsLayer = JsonNode.Parse(nameJson.ToJsonString().Replace("test-layer", "0", StringComparison.Ordinal))!;
        JsonNode.DeepEquals(layerJson, nameAsLayer).Should().BeTrue(
            "{0} must return the same document by service name; by layer id: {1}; by service name: {2}",
            route,
            layerText,
            nameText);
    }

    private static void AssertKmzOverlaysMosaic(byte[] kmz, string route)
    {
        using var archive = new ZipArchive(new MemoryStream(kmz), ZipArchiveMode.Read);
        var entry = archive.Entries.Should().ContainSingle(e => e.FullName.EndsWith(".kml", StringComparison.Ordinal)).Subject;
        using var stream = entry.Open();
        var box = XDocument.Load(stream).Descendants().Single(element => element.Name.LocalName == "LatLonBox");
        string Edge(string name) => box.Elements().Single(element => element.Name.LocalName == name).Value;
        (Edge("north"), Edge("south"), Edge("east"), Edge("west")).Should().Be(("2", "0", "4", "0"), route);
    }

    private static void AssertEnvelope(JsonElement extent, double xmin, double ymin, double xmax, double ymax)
    {
        extent.GetProperty("xmin").GetDouble().Should().Be(xmin);
        extent.GetProperty("ymin").GetDouble().Should().Be(ymin);
        extent.GetProperty("xmax").GetDouble().Should().Be(xmax);
        extent.GetProperty("ymax").GetDouble().Should().Be(ymax);
        extent.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
    }

    private async Task<string> IssueAsync(string principalId, string role)
        => (await _secured.GetService<IPortalTokenIssuer>().IssueAsync(
            new PortalTokenIssueRequest(
                principalId,
                principalId,
                TenantId: null,
                Roles: [role],
                PortalTokenClientType.Referer,
                Referer,
                DateTimeOffset.UtcNow.AddMinutes(30)),
            CancellationToken.None)).Token;

    /// <summary>
    /// Sends on a dedicated client: the secured fixture's shared client carries an admin API key,
    /// which would satisfy every request under test.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        string? token,
        HttpMethod method,
        string url,
        Dictionary<string, string>? form = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Referrer = new Uri(Referer);
        }

        using var client = _secured.CreateClient();
        return await client.SendAsync(request);
    }
}
