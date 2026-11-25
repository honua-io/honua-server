// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NSubstitute;
using static Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer.ImageServerCogStatisticsData;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

// computeStatisticsHistograms and legend on a layer whose imagery is only a registered cloud COG
// read the COG's coarsest overview instead of answering "No rasters found".
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerCogStatisticsTests
{
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly ICogStore _cogStore = Substitute.For<ICogStore>();
    private readonly ICogTileResolver _tileResolver = Substitute.For<ICogTileResolver>();

    public ImageServerCogStatisticsTests()
    {
        _rasterStore.QueryRastersAsync(default, default, default).ReturnsForAnyArgs(Array.Empty<RasterInfo>());
        _rasterStore.ListRastersAsync(default, default).ReturnsForAnyArgs(Array.Empty<RasterInfo>());
        _cogStore.ListByLayerAsync(1, Arg.Any<CancellationToken>()).Returns(new[] { Registration() });
        ServeOverview(_tileResolver);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeStatisticsHistograms_CogOnlyLayer_DescribesTheOverviewSamples()
    {
        var response = await ComputeAsync(new() { ["histogramParameters"] = "{\"size\":4}" });

        response.Statistics.Should().HaveCount(3);
        response.Statistics[0].Min.Should().Be(10);
        response.Statistics[0].Max.Should().Be(30);
        response.Statistics[0].Mean.Should().BeApproximately(20, 1e-9);
        response.Statistics[0].StandardDeviation.Should().BeApproximately(10, 1e-9);
        response.Statistics[0].Count.Should().Be(TileSize * TileSize);
        response.Histograms[0].Counts.Should().Equal(32768, 0, 0, 32768);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeStatisticsHistograms_CogNoDataSamples_AreNotCounted()
    {
        var response = await ComputeAsync(new() { ["bandIds"] = "2" });

        response.Statistics.Should().ContainSingle();
        response.Statistics[0].Min.Should().Be(7);
        response.Statistics[0].Max.Should().Be(7);
        response.Statistics[0].Count.Should().Be((TileSize - 64) * TileSize);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeStatisticsHistograms_CogOnlyLayerWithGeometry_AnswersNotImplemented()
    {
        var context = CreateContext();
        var values = new Dictionary<string, StringValues> { ["geometry"] = "{\"xmin\":0,\"ymin\":0,\"xmax\":1,\"ymax\":1}" };

        var result = await StatisticsHandler().ComputeAsync(context, 1, values, CancellationToken.None);

        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status501NotImplemented);
        await _tileResolver.DidNotReceiveWithAnyArgs().GetTileAsync(default!, default, default, default, default, default);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeStatisticsHistograms_CogOnlyLayerWithMosaicRule_AnswersNotImplemented()
    {
        var context = CreateContext();
        var values = new Dictionary<string, StringValues> { ["mosaicRule"] = "{\"mosaicMethod\":\"esriMosaicNone\"}" };

        var result = await StatisticsHandler().ComputeAsync(context, 1, values, CancellationToken.None);

        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status501NotImplemented);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeStatisticsHistograms_CogNeedingTooManyTileReads_AnswersNotImplementedWithoutReading()
    {
        _cogStore.ListByLayerAsync(1, Arg.Any<CancellationToken>()).Returns(new[] { WideRegistration() });
        var context = CreateContext();

        var result = await StatisticsHandler().ComputeAsync(context, 1, new Dictionary<string, StringValues>(), CancellationToken.None);

        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status501NotImplemented);
        await _tileResolver.DidNotReceiveWithAnyArgs().GetTileAsync(default!, default, default, default, default, default);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeStatisticsHistograms_OverlappingCogs_CountEachPixelOnceForTheFirstCog()
    {
        var second = Registration() with { Id = 8 };
        _cogStore.ListByLayerAsync(1, Arg.Any<CancellationToken>()).Returns(new[] { Registration(), second });
        var samples = Enumerable.Repeat((byte)99, TileSize * TileSize * 3).ToArray();
        _tileResolver.GetTileAsync(Arg.Is<CogRegistration>(registration => registration.Id == 8), 11, 791, 327, RasterFormat.Raw, Arg.Any<CancellationToken>())
            .Returns(RawTile(samples));

        var response = await ComputeAsync(new() { ["bandIds"] = "0" });

        response.Statistics[0].Max.Should().Be(30, "the second COG lies under the first everywhere");
        response.Statistics[0].Count.Should().Be(TileSize * TileSize);
    }

    [UnitTest]
    public void Compute_Float32NoData_IsMatchedAtFloat32Precision()
    {
        var registration = Registration() with
        {
            Metadata = Registration().Metadata! with { BandCount = 1, PixelType = "float32", BitsPerSample = 32, NoData = "-9999.9" },
        };
        var samples = new byte[TileSize * TileSize * 4];
        for (var index = 0; index < TileSize * TileSize; index++)
        {
            BitConverter.GetBytes(index % 2 == 0 ? -9999.9f : 2.5f).CopyTo(samples, index * 4);
        }

        var block = new ImageServerCogStatistics.CogPixelBlock(new(0, registration, new(11, 327, 791)), samples, []);
        var result = ImageServerCogStatistics.Compute([block], [1], 4);

        result.Statistics[0].ValidPixelCount.Should().Be(TileSize * TileSize / 2);
        result.Statistics[0].MinValue.Should().Be(2.5);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeStatisticsHistograms_CogOnlyLayerWithoutEntitlement_AnswersPaymentRequired()
    {
        var context = CreateContext(services => services.AddSingleton(WithoutEntitlement()));

        var result = await StatisticsHandler().ComputeAsync(context, 1, new Dictionary<string, StringValues>(), CancellationToken.None);

        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status402PaymentRequired);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeStatisticsHistograms_LayerWithoutRastersOrCogs_StillAnswersNotFound()
    {
        _cogStore.ListByLayerAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<CogRegistration>());

        var context = CreateContext();

        var result = await StatisticsHandler().ComputeAsync(context, 1, new Dictionary<string, StringValues>(), CancellationToken.None);

        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeStatisticsHistograms_RestWithoutGeometryOnCogOnlyLayer_AnswersFromTheCog()
    {
        var context = CreateContext();

        var result = await StatisticsHandler().ComputeAsync(context, new StatisticsRequest { LayerId = 1, Values = new Dictionary<string, StringValues>(), Aoi = StatisticsAoi.Required }, CancellationToken.None);

        var response = result.Should().BeOfType<JsonHttpResult<ComputeStatisticsHistogramsResponse>>().Which.Value!;
        response.Statistics.Should().HaveCount(3);
        response.Statistics[0].Min.Should().Be(10);
    }

    [UnitTheory]
    [Operation(Operations.Query)]
    [InlineData("mosaicRule", "{\"mosaicMethod\":\"esriMosaicNone\"}")]
    public async Task ComputeHistograms_RestWithUnappliedParameterButNoGeometryOnCogOnlyLayer_AnswersNotImplemented(string name, string value)
    {
        var context = CreateContext();
        var values = new Dictionary<string, StringValues> { [name] = value };

        var result = await StatisticsHandler().ComputeHistogramsAsync(context, new StatisticsRequest { LayerId = 1, Values = values, Aoi = StatisticsAoi.Required }, CancellationToken.None);

        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status501NotImplemented);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeStatisticsHistograms_RestWithoutGeometryOnLayerWithoutCogs_AnswersGeometryRequired()
    {
        _cogStore.ListByLayerAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<CogRegistration>());
        var context = CreateContext();

        var result = await StatisticsHandler().ComputeAsync(context, new StatisticsRequest { LayerId = 1, Values = new Dictionary<string, StringValues>(), Aoi = StatisticsAoi.Required }, CancellationToken.None);

        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status400BadRequest);
        await _rasterStore.DidNotReceiveWithAnyArgs().QueryRastersAsync(default, default, default);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeStatisticsHistograms_RestWithoutGeometryOnCatalogLayer_AnswersGeometryRequiredWithoutCogLookup()
    {
        _rasterStore.GetPrimaryRasterInfoAsync(1, Arg.Any<CancellationToken>()).Returns((RasterInfo?)ImageServerCogRasters.Describe(Registration()));
        var context = CreateContext();

        var result = await StatisticsHandler().ComputeAsync(context, new StatisticsRequest { LayerId = 1, Values = new Dictionary<string, StringValues>(), Aoi = StatisticsAoi.Required }, CancellationToken.None);

        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status400BadRequest);
        await _cogStore.DidNotReceiveWithAnyArgs().ListByLayerAsync(default, default);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task Legend_CogOnlyLayer_StretchesOverTheCogBandOneRange()
    {
        var handler = new ImageServerLegendHandler(
            ImageServerTestFixtures.BuildGraphWithLayer(1),
            _rasterStore,
            new ImageServerLegendSwatchBuilder(),
            NullLogger<ImageServerLegendHandler>.Instance);

        var result = await handler.GetLegendAsync(CreateContext(), 1, CancellationToken.None, renderingRule: string.Empty);

        var legend = result.Should().BeOfType<JsonHttpResult<LegendResponse>>().Which.Value!.Layers.Single();
        legend.LegendType.Should().Be("Stretched");
        legend.Legend.First().Values.Should().Equal(10, 14);
        legend.Legend.Last().Values.Should().Equal(26, 30);
    }

    [UnitTest]
    public void IsReadable_JpegCog_IsNotReadForStatistics()
    {
        var registration = Registration();
        var jpeg = registration with { Metadata = registration.Metadata! with { Compression = "JPEG" } };

        ImageServerCogStatistics.IsReadable(registration).Should().BeTrue();
        ImageServerCogStatistics.IsReadable(jpeg).Should().BeFalse();
    }

    [UnitTest]
    public void CacheKey_PartitionsByTenantSchemaBucketAndObjectKey()
    {
        var baseRegistration = Registration();
        var otherBucket = baseRegistration with { Bucket = "other-bucket" };
        var otherObjectKey = baseRegistration with { ObjectKey = "other/cog.tif" };

        HttpContext BuildContext(string? tenantId, string? schema)
            => CreateContext(services =>
            {
                var tenant = Substitute.For<ITenantContext>();
                tenant.TenantId.Returns(tenantId);
                services.AddSingleton(tenant);

                var schemaContext = Substitute.For<ISchemaContext>();
                schemaContext.CurrentSchema.Returns(schema);
                services.AddSingleton(schemaContext);
            });

        var baseline = ImageServerCogStatistics.CacheKey(BuildContext("tenant-a", "schema_a"), new([baseRegistration], [1]), 4);
        var differentTenant = ImageServerCogStatistics.CacheKey(BuildContext("tenant-b", "schema_a"), new([baseRegistration], [1]), 4);
        var differentSchema = ImageServerCogStatistics.CacheKey(BuildContext("tenant-a", "schema_b"), new([baseRegistration], [1]), 4);
        var differentBucket = ImageServerCogStatistics.CacheKey(BuildContext("tenant-a", "schema_a"), new([otherBucket], [1]), 4);
        var differentObject = ImageServerCogStatistics.CacheKey(BuildContext("tenant-a", "schema_a"), new([otherObjectKey], [1]), 4);

        new[] { baseline, differentTenant, differentSchema, differentBucket, differentObject }
            .Should().OnlyHaveUniqueItems();
    }

    private async Task<ComputeStatisticsHistogramsResponse> ComputeAsync(Dictionary<string, StringValues> values)
    {
        var result = await StatisticsHandler().ComputeAsync(CreateContext(), 1, values, CancellationToken.None);
        return result.Should().BeOfType<JsonHttpResult<ComputeStatisticsHistogramsResponse>>().Which.Value!;
    }

    private ImageServerStatisticsHistogramsHandler StatisticsHandler()
        => new(ImageServerTestFixtures.BuildGraphWithLayer(1), _rasterStore, NullLogger<ImageServerStatisticsHistogramsHandler>.Instance);

    private DefaultHttpContext CreateContext(Action<IServiceCollection>? configure = null)
        => ImageServerTestFixtures.CreateContext("/rest/services/1/ImageServer/computeStatisticsHistograms", services =>
        {
            services.AddSingleton(_cogStore);
            services.AddSingleton(_tileResolver);
            configure?.Invoke(services);
        });
}
