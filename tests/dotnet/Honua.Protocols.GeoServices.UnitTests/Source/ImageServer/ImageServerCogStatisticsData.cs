// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Services;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

// The bounded-roster COG (512 x 512 at 38.2185 m, 256-pixel tiles, NoData 0) bound to layer 1. Its
// coarsest overview (256 x 256) is the single zoom-11 grid tile 327/791, whose samples are:
// band 1 = 10 in the top half and 30 in the bottom half, band 2 = 50, band 3 = NoData in the top
// 64 rows and 7 below.
internal static class ImageServerCogStatisticsData
{
    public const int TileSize = 256;

    public static readonly RasterExtent CogExtent = new()
    {
        XMin = -13638811.83098057,
        YMin = 4539747.983913188,
        XMax = -13619243.951739565,
        YMax = 4559315.863154193,
    };

    public static CogRegistration Registration() => new()
    {
        Id = 7,
        LayerId = 1,
        Name = "roster cog",
        Provider = CloudStorageProvider.AwsS3,
        Bucket = "roster-fixtures",
        ObjectKey = "cog/roster-3857.tif",
        Metadata = new CogMetadata(
            512, 512, 3, "uint8", 3857, "DEFLATE", TileSize, TileSize,
            [new(0, 512, 512, 0, [], []), new(1, 256, 256, 0, [], [])],
            CogExtent,
            NoData: "0"),
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    public static void ServeOverview(ICogTileResolver resolver)
        => resolver.GetTileAsync(Arg.Is<CogRegistration>(registration => registration.Id == 7), 11, 791, 327, RasterFormat.Raw, Arg.Any<CancellationToken>())
            .Returns(RawTile(OverviewSamples()));

    public static RasterResult RawTile(byte[] samples) => new()
    {
        Data = samples,
        ContentType = "application/octet-stream",
        Width = TileSize,
        Height = TileSize,
    };

    // A COG without overviews spanning 5 x 5 zoom-12 tiles: its only servable zoom needs 25 reads.
    public static CogRegistration WideRegistration()
    {
        var span = CogExtent.XMax - CogExtent.XMin;
        var extent = CogExtent with { XMax = CogExtent.XMin + (span * 2.5), YMin = CogExtent.YMax - (span * 2.5) };
        var registration = Registration();
        return registration with
        {
            Metadata = registration.Metadata! with { Width = 1280, Height = 1280, OverviewLevels = [], Extent = extent },
        };
    }

    public static ILicenseEntitlementService WithoutEntitlement()
    {
        var licensing = Substitute.For<ILicenseEntitlementService>();
        licensing.CheckEntitlement(ImageServerCogRasters.Entitlement).Returns(new LicenseEntitlementDecision(
            ImageServerCogRasters.Entitlement, false, default, default, RequiredEdition: null, UpgradeMessage: "not active"));
        return licensing;
    }

    private static byte[] OverviewSamples()
    {
        var samples = new byte[TileSize * TileSize * 3];
        for (var row = 0; row < TileSize; row++)
        {
            for (var column = 0; column < TileSize; column++)
            {
                var offset = ((row * TileSize) + column) * 3;
                samples[offset] = row < TileSize / 2 ? (byte)10 : (byte)30;
                samples[offset + 1] = 50;
                samples[offset + 2] = row < 64 ? (byte)0 : (byte)7;
            }
        }

        return samples;
    }
}
