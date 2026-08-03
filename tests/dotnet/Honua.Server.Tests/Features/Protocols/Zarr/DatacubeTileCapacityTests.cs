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
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Zarr;

public sealed class DatacubeTileCapacityTests
{
    [Fact]
    public async Task HandleDatacubeTile_CapacityDenialOccursBeforeRangeResolutionOrSubsetRead()
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
        var capacity = new DenyingAdmission();

        var result = await Honua.Server.Features.Protocols.Zarr.ZarrEndpoints.HandleDatacubeTile(
            new DefaultHttpContext(),
            7,
            TileMatrixSetRegistry.WebMercatorQuadId,
            0,
            0,
            0,
            store,
            subsetReader,
            Array.Empty<ICloudRangeReader>(),
            tileMatrixSets,
            capacity,
            tenant,
            NullLogger<Honua.Server.Features.Protocols.Zarr.ZarrEndpointsLog>.Instance,
            CancellationToken.None);

        result.Should().NotBeNull();
        capacity.Request.Should().NotBeNull();
        capacity.Request!.TenantPartition.Should().Be("tenant-a");
        capacity.Request.Work.ObjectRangeRequests.Should().Be(1);
        capacity.Request.Work.ObjectRangeBytes.Should().Be(256L * 256L * sizeof(float));
        capacity.Request.Work.PostGisWorkUnits.Should().Be(0);
        await subsetReader.DidNotReceiveWithAnyArgs().ReadSubsetAsync(
            default!, default!, default!, default!, default!, default);
    }

    private sealed class DenyingAdmission : IRasterCapacityAdmission
    {
        public RasterCapacityRequest? Request { get; private set; }

        public ValueTask<RasterCapacityAdmissionResult> TryAcquireAsync(
            RasterCapacityRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return ValueTask.FromResult(RasterCapacityAdmissionResult.Denied(
                RasterCapacityDenialKind.WorkLimitExceeded,
                RasterCapacityDimension.ObjectRangeBytes,
                request.Work.ObjectRangeBytes,
                request.Work.ObjectRangeBytes - 1,
                request.OverflowAction));
        }
    }
}
