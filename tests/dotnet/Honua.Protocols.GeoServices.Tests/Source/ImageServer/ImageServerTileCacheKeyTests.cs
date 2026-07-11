// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Unit coverage for <see cref="ImageServerTileCacheKey"/> proving the durable tile-cache key
/// varies with every render-affecting dimension the WMTS matrix-set feature introduced (#2665):
/// tile matrix set, style, output format, time, tenant/auth identity, and layer identity. A
/// collision on any of these would serve the wrong bytes across gridsets, styles, tenants, or times.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public class ImageServerTileCacheKeyTests
{
    private static readonly RasterInfo[] Rasters = new[]
    {
        new RasterInfo
        {
            Id = 100,
            LayerId = 7,
            Name = "r",
            Width = 256,
            Height = 256,
            BandCount = 1,
            PixelType = "8BUI",
            Srid = 4326,
            GeoTransform = [-180, 1.40625, 0, 90, 0, -0.703125],
            Extent = new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 },
            CreatedAt = DateTimeOffset.UnixEpoch,
        },
    };

    private static string Build(
        string tileMatrixSetId = "WebMercatorQuad",
        string styleId = "default",
        string tenantAuthKey = "",
        int layerId = 7,
        RasterFormat format = RasterFormat.PNG,
        DateTimeOffset? timestamp = null,
        IReadOnlyList<RasterInfo>? rasters = null,
        int level = 3,
        int row = 2,
        int col = 1)
        => ImageServerTileCacheKey.Build(
            storageOptions: null,
            metadataEtag: "etag-1",
            layerId: layerId,
            tileMatrixSetId: tileMatrixSetId,
            styleId: styleId,
            tenantAuthKey: tenantAuthKey,
            selectedRasters: rasters ?? Rasters,
            mergeStrategy: RasterMergeStrategy.Newest,
            timestamp: timestamp,
            mosaicRule: string.Empty,
            rasterFormat: format,
            level: level,
            row: row,
            col: col);

    [UnitTest]
    public void Build_IsDeterministic_ForIdenticalInputs()
    {
        Build().Should().Be(Build());
    }

    [UnitTest]
    public void Build_VariesByTileMatrixSet()
    {
        Build(tileMatrixSetId: "WebMercatorQuad").Should().NotBe(Build(tileMatrixSetId: "WorldCRS84Quad"));
    }

    [UnitTest]
    public void Build_VariesByStyle()
    {
        Build(styleId: "default").Should().NotBe(Build(styleId: "night"));
    }

    [UnitTest]
    public void Build_VariesByFormat()
    {
        Build(format: RasterFormat.PNG).Should().NotBe(Build(format: RasterFormat.JPEG));
    }

    [UnitTest]
    public void Build_VariesByTime()
    {
        Build(timestamp: null)
            .Should().NotBe(Build(timestamp: DateTimeOffset.Parse("2020-06-01T00:00:00Z", CultureInfo.InvariantCulture)));
    }

    [UnitTest]
    public void Build_VariesByTenantAuthIdentity()
    {
        Build(tenantAuthKey: string.Empty).Should().NotBe(Build(tenantAuthKey: "tenant-a|user-1"));
        Build(tenantAuthKey: "tenant-a|user-1").Should().NotBe(Build(tenantAuthKey: "tenant-b|user-2"));
    }

    [UnitTest]
    public void Build_VariesByLayerIdentity()
    {
        // Layer index and the participating raster set are both part of the layer identity.
        Build(layerId: 7).Should().NotBe(Build(layerId: 8));

        var otherRasters = new[] { Rasters[0] with { Id = 999 } };
        Build(rasters: Rasters).Should().NotBe(Build(rasters: otherRasters));
    }

    [UnitTest]
    public void Build_VariesByTileCoordinates()
    {
        Build(level: 3, row: 2, col: 1).Should().NotBe(Build(level: 4, row: 2, col: 1));
        Build(level: 3, row: 2, col: 1).Should().NotBe(Build(level: 3, row: 3, col: 1));
        Build(level: 3, row: 2, col: 1).Should().NotBe(Build(level: 3, row: 2, col: 2));
    }
}
