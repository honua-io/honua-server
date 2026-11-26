// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Protocols.OgcApi.UnitTests.Features.Services;

[Protocol(TestProtocols.OgcApiFeatures)]
[Operation(Operations.Create)]
public sealed class OgcFeatureGeographicBoundsTests
{
    [UnitTest]
    public async Task TryBuildFeatureAsync_OutOfRangePointOnEpsg4326Layer_ReturnsValidationFailure()
    {
        var geometryServices = CreateGeometryServices(999d, 999d, 4326);
        var mutationValidator = CreateMutationValidator();
        var feature = new GeoJsonFeature
        {
            Geometry = new SimpleGeoJsonGeometry { Type = "Point", CoordinatesJson = "[999,999]" }
        };

        var crs84 = new CrsDefinition(
            "http://www.opengis.net/def/crs/OGC/1.3/CRS84",
            4326,
            AxisOrder.EastNorth,
            IsGeographic: true);

        var result = await OgcFeatureMutationHelpers.TryBuildFeatureAsync(
            LayerResource(MetadataV2SpatialReference.Wgs84),
            feature,
            crs84,
            geometryServices,
            mutationValidator,
            objectId: 0,
            isUpdate: false,
            CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Longitude 999")
            .And.Contain("latitude 999")
            .And.Contain("EPSG:4326");
    }

    [UnitTest]
    public async Task TryBuildFeatureAsync_ProjectedCoordinatesOnEpsg3857Layer_Succeeds()
    {
        var geometryServices = CreateGeometryServices(-17572680d, 2428540d, 3857);
        var mutationValidator = CreateMutationValidator();
        var feature = new GeoJsonFeature
        {
            Geometry = new SimpleGeoJsonGeometry { Type = "Point", CoordinatesJson = "[-17572680,2428540]" }
        };
        var epsg3857 = new CrsDefinition(
            "http://www.opengis.net/def/crs/EPSG/0/3857",
            3857,
            AxisOrder.EastNorth,
            IsGeographic: false);

        var result = await OgcFeatureMutationHelpers.TryBuildFeatureAsync(
            LayerResource(MetadataV2SpatialReference.WebMercator),
            feature,
            epsg3857,
            geometryServices,
            mutationValidator,
            objectId: 0,
            isUpdate: false,
            CancellationToken.None);

        result.IsValid.Should().BeTrue();
        result.Feature.Should().NotBeNull();
    }

    private static OgcFeaturesGeometryServices CreateGeometryServices(double x, double y, int srid)
    {
        var point = new Point(x, y) { SRID = srid };
        var wkb = new WKBWriter(ByteOrder.LittleEndian, handleSRID: true).Write(point);
        var geometryService = Substitute.For<IGeometryService>();
        geometryService.ConvertGeoJsonToWkb(Arg.Any<string>(), Arg.Any<int?>()).Returns(wkb);
        return new OgcFeaturesGeometryServices(
            geometryService,
            Substitute.For<ICoordinateTransformService>(),
            Options.Create(new LimitsOptions()));
    }

    private static FeatureMutationValidator CreateMutationValidator()
    {
        var geometryValidator = Substitute.For<IGeometryValidator>();
        geometryValidator.ValidateCompleteAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GeometryValidationResult.Success()));
        return new FeatureMutationValidator(geometryValidator);
    }

    private static MetadataV2Object LayerResource(MetadataV2SpatialReference spatialReference) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "216", Name = "points" },
        Type = MetadataV2ObjectType.FeatureDataset,
        Spatial = new MetadataV2ObjectSpatial
        {
            SpatialReference = spatialReference,
            GeometryType = MetadataV2GeometryType.Point
        }
    };
}
