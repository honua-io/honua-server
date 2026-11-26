// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Validation;
using Honua.TestKit.Attributes;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Validation;

[Protocol(TestProtocols.FeatureServer)]
[Protocol(TestProtocols.OgcApiFeatures)]
[Protocol(TestProtocols.Grpc)]
[Protocol(TestProtocols.Wfs20)]
[Operation(Operations.Create)]
public sealed class FeatureMutationGeographicBoundsTests
{
    [UnitTheory]
    [InlineData("POINT (999 999)", "Longitude 999", "latitude 999")]
    [InlineData("POINT (-180.001 0)", "Longitude -180.001", "[-180, 180]")]
    [InlineData("POINT (180.001 0)", "Longitude 180.001", "[-180, 180]")]
    [InlineData("POINT (0 -90.001)", "Latitude -90.001", "[-90, 90]")]
    [InlineData("POINT (0 90.001)", "Latitude 90.001", "[-90, 90]")]
    [InlineData("LINESTRING (0 0, 10 95)", "Latitude 95", "[-90, 90]")]
    [InlineData("POLYGON ((0 0, 10 0, 10 10, 200 10, 0 0))", "Longitude 200", "[-180, 180]")]
    public async Task ValidateGeometryAsync_OutOfRangeGeographicCoordinate_IsRejected(
        string wkt, string expectedAxisDetail, string expectedRangeDetail)
    {
        var validator = CreateValidator();

        var result = await validator.ValidateGeometryAsync(
            Resource(MetadataV2SpatialReference.Wgs84, MetadataV2GeometryType.None),
            Wkb(wkt),
            CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain(expectedAxisDetail)
            .And.Contain(expectedRangeDetail)
            .And.Contain("EPSG:4326");
    }

    [UnitTest]
    public async Task ValidateGeometryAsync_Crs84LayerWithOutOfRangePoint_IsRejected()
    {
        var validator = CreateValidator();
        var crs84 = new MetadataV2SpatialReference
        {
            Crs = "http://www.opengis.net/def/crs/OGC/1.3/CRS84",
            IsGeographic = true
        };

        var result = await validator.ValidateGeometryAsync(
            Resource(crs84, MetadataV2GeometryType.Point),
            Wkb("POINT (999 999)"),
            CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Longitude 999").And.Contain("latitude 999");
    }

    [UnitTest]
    public async Task ValidateGeometryAsync_Nad83GeographicLayerOutOfRange_ReportsLayerSrid()
    {
        var validator = CreateValidator();
        var nad83 = new MetadataV2SpatialReference { Srid = 4269, IsGeographic = true };

        var result = await validator.ValidateGeometryAsync(
            Resource(nad83, MetadataV2GeometryType.Point),
            Wkb("POINT (250 10)"),
            CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Longitude 250").And.Contain("EPSG:4269");
    }

    [UnitTheory]
    [InlineData("POINT (-180 -90)")]
    [InlineData("POINT (180 90)")]
    [InlineData("POINT (0 0)")]
    [InlineData("POINT Z (86.925 27.9881 8848.86)")]
    [InlineData("POINT ZM (-157.8583 21.3069 500 9999)")]
    public async Task ValidateGeometryAsync_InRangeGeographicCoordinates_AreAccepted(string wkt)
    {
        var validator = CreateValidator();

        var result = await validator.ValidateGeometryAsync(
            Resource(MetadataV2SpatialReference.Wgs84, MetadataV2GeometryType.Point),
            Wkb(wkt),
            CancellationToken.None);

        result.IsValid.Should().BeTrue();
    }

    [UnitTest]
    public async Task ValidateGeometryAsync_ProjectedLayerWithLargeCoordinates_IsAccepted()
    {
        var validator = CreateValidator();

        var result = await validator.ValidateGeometryAsync(
            Resource(MetadataV2SpatialReference.WebMercator, MetadataV2GeometryType.Point),
            Wkb("POINT (-17572680 2428540)"),
            CancellationToken.None);

        result.IsValid.Should().BeTrue();
    }

    private static FeatureMutationValidator CreateValidator()
    {
        var geometryValidator = Substitute.For<IGeometryValidator>();
        geometryValidator.ValidateCompleteAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GeometryValidationResult.Success()));
        return new FeatureMutationValidator(geometryValidator);
    }

    private static MetadataV2Object Resource(
        MetadataV2SpatialReference spatialReference,
        MetadataV2GeometryType geometryType) => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "layer", Name = "layer" },
            Spatial = new MetadataV2ObjectSpatial
            {
                SpatialReference = spatialReference,
                GeometryType = geometryType
            }
        };

    private static byte[] Wkb(string wkt) => new WKBWriter().Write(new WKTReader().Read(wkt));
}
