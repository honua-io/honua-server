// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerComputeTiePointsScopeTests
{
    private const int LayerId = 7;
    private const long PrimaryRasterId = 100;
    private const long SameLayerRasterId = 101;
    private const long OtherLayerRasterId = 900;

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeTiePoints_RasterIdFromAnotherLayer_ReadsNoSensorMetadata()
    {
        var store = CreateStore();

        var context = CreateContext();

        var result = await ComputeAsync(store, OtherLayerRasterId, context);

        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status404NotFound);
        await store.DidNotReceive().GetSensorMetadataAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeTiePoints_RasterIdInTheLayer_ReadsItsSensorMetadata()
    {
        var store = CreateStore();

        await ComputeAsync(store, SameLayerRasterId, CreateContext());

        await store.Received(1).GetSensorMetadataAsync(
            Arg.Is<IReadOnlyCollection<long>>(ids => ids.Contains(SameLayerRasterId)),
            Arg.Any<CancellationToken>());
    }

    private static async Task<IResult> ComputeAsync(IRasterStore store, long rasterId, HttpContext context)
    {
        var handler = new ImageServerComputeTiePointsHandler(store, NullLogger<ImageServerComputeTiePointsHandler>.Instance);
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["rasterId"] = rasterId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return await handler.ComputeTiePointsAsync(context, LayerId, values, CancellationToken.None);
    }

    private static DefaultHttpContext CreateContext()
        => ImageServerTestFixtures.CreateContext($"/rest/services/{LayerId}/ImageServer/computeTiePoints");

    private static IRasterStore CreateStore()
    {
        var store = Substitute.For<IRasterStore>();
        store.GetPrimaryRasterInfoAsync(LayerId, Arg.Any<CancellationToken>()).Returns(CreateRaster(PrimaryRasterId));
        store.GetRasterInfoAsync(LayerId, SameLayerRasterId, Arg.Any<CancellationToken>()).Returns(CreateRaster(SameLayerRasterId));
        store.GetRasterInfoAsync(LayerId, OtherLayerRasterId, Arg.Any<CancellationToken>()).Returns((RasterInfo?)null);
        store.GetSensorMetadataAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, RasterSensorMetadata>());
        return store;
    }

    private static RasterInfo CreateRaster(long id) => new()
    {
        Id = id,
        LayerId = LayerId,
        Name = "raster-" + id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Width = 256,
        Height = 256,
        BandCount = 1,
        PixelType = "8BUI",
        Srid = 4326,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };
}
