// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Capacity;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Tiles;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Zarr;

public sealed class DatacubeTileCapacityTests
{
    [Fact]
    public async Task HandleDatacubeTile_CapacityDenialOccursBeforeRangeResolutionOrSubsetRead()
    {
        var context = new DefaultHttpContext();
        var capacity = new DenyingAdmission();

        var (_, subsetReader) = await InvokeEndpointAsync(context, capacity);

        capacity.Request.Should().NotBeNull();
        capacity.Request!.TenantPartition.Should().Be("tenant-a");
        capacity.Request.Work.ObjectRequests.Should().Be(1);
        capacity.Request.Work.ObjectRangeBytes.Should().Be(256L * 256L * sizeof(float));
        capacity.Request.Work.PostGisWorkUnits.Should().Be(0);
        await subsetReader.DidNotReceiveWithAnyArgs().ReadSubsetAsync(
            default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task HandleDatacubeTile_StaticWorkDenial_Returns413WithDurableGuidance()
    {
        var context = CreateExecutableContext();
        var capacity = new DenyingAdmission();

        var (result, subsetReader) = await InvokeEndpointAsync(context, capacity);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        ReadResponseBody(context).Should().Contain("durable raster geoprocessing job");
        await subsetReader.DidNotReceiveWithAnyArgs().ReadSubsetAsync(
            default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task HandleDatacubeTile_ConcurrencyDenial_Returns429WithRetryAfter()
    {
        var context = CreateExecutableContext();
        var capacity = new DenyingAdmission(
            RasterCapacityDenialKind.TenantConcurrencyExceeded,
            RasterCapacityDimension.TenantConcurrency,
            retryAfterSeconds: 7);

        var (result, subsetReader) = await InvokeEndpointAsync(context, capacity);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        context.Response.Headers.RetryAfter.ToString().Should().Be("7");
        ReadResponseBody(context).Should().Contain("durable raster geoprocessing job");
        await subsetReader.DidNotReceiveWithAnyArgs().ReadSubsetAsync(
            default!, default!, default!, default!, default!, default);
    }

    private static async Task<(IResult Result, IZarrSubsetReader SubsetReader)> InvokeEndpointAsync(
        DefaultHttpContext context,
        DenyingAdmission capacity)
    {
        var tileMatrixSets = new TileMatrixSetRegistry(new TileMatrixSetDefinitionOptions());
        tileMatrixSets.TryGetGeometry(TileMatrixSetRegistry.WebMercatorQuadId, 0, out var geometry).Should().BeTrue();
        var bounds = geometry!.GetTileBounds(0, 0, 0)!;
        var array = new ZarrArrayMetadata(
            "temperature",
            ZarrFormatVersion.V2,
            string.Empty,
            [256, 256],
            [256, 256],
            "<f4",
            "C",
            null,
            null,
            ["y", "x"]);
        var registration = new ZarrRegistration
        {
            Id = 1,
            LayerId = 7,
            Name = "capacity-test",
            Provider = CloudStorageProvider.AwsS3,
            Bucket = "bucket",
            RootPath = "zarr",
            Metadata = new ZarrStoreMetadata(
                ZarrFormatVersion.V2,
                3857,
                new RasterExtent
                {
                    XMin = bounds.XMin,
                    YMin = bounds.YMin,
                    XMax = bounds.XMax,
                    YMax = bounds.YMax,
                    Srid = 3857,
                },
                [array],
                array.Name,
                "x",
                "y",
                null),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var store = Substitute.For<IZarrStore>();
        store.ListByLayerAsync(7, Arg.Any<CancellationToken>()).Returns([registration]);
        var subsetReader = Substitute.For<IZarrSubsetReader>();
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-a");
        var tileService = new Honua.Server.Features.Protocols.Zarr.ZarrDatacubeTileService(
            store,
            subsetReader,
            Array.Empty<ICloudRangeReader>(),
            tileMatrixSets,
            capacity,
            tenant,
            NullLogger<Honua.Server.Features.Protocols.Zarr.ZarrEndpointsLog>.Instance);

        var result = await Honua.Server.Features.Protocols.Zarr.ZarrEndpoints.HandleDatacubeTile(
            context,
            7,
            TileMatrixSetRegistry.WebMercatorQuadId,
            0,
            0,
            0,
            tileService,
            CancellationToken.None);

        result.Should().NotBeNull();
        return (result, subsetReader);
    }

    private static DefaultHttpContext CreateExecutableContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/zarr/layers/7/tiles/WebMercatorQuad/0/0/0";
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("api.example.com");
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        context.Features.Set<IHttpResponseFeature>(new HttpResponseFeature());
        return context;
    }

    private static string ReadResponseBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return reader.ReadToEnd();
    }

    private sealed class DenyingAdmission(
        RasterCapacityDenialKind denialKind = RasterCapacityDenialKind.WorkLimitExceeded,
        RasterCapacityDimension dimension = RasterCapacityDimension.ObjectRangeBytes,
        int? retryAfterSeconds = null) : IRasterCapacityAdmission
    {
        public RasterCapacityRequest? Request { get; private set; }

        public ValueTask<RasterCapacityAdmissionResult> TryAcquireAsync(
            RasterCapacityRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            var requested = denialKind == RasterCapacityDenialKind.WorkLimitExceeded
                ? request.Work.ObjectRangeBytes
                : 1;
            return ValueTask.FromResult(RasterCapacityAdmissionResult.Denied(
                denialKind,
                dimension,
                requested,
                Math.Max(1, requested - 1),
                request.OverflowAction,
                retryAfterSeconds));
        }
    }
}
