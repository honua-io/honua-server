// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Validation;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Validation;

/// <summary>
/// #6108: the shared <see cref="FeatureMutationValidator"/> enforces the layer's declared
/// geometry type on the post-repair geometry for every protocol adapter.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Protocol(TestProtocols.OgcApiFeatures)]
[Protocol(TestProtocols.Wfs20)]
[Operation(Operations.Create)]
public sealed class FeatureMutationGeometryTypeTests
{
    private const string CollinearPolygon = "POLYGON ((0 0, 1 1, 2 2, 0 0))";

    [UnitTest]
    public async Task ValidateGeometryAsync_RepairChangesPolygonToLineString_IsRejected()
    {
        var validator = CreateValidator(
            GeometryValidationResult.SuccessWithRepair(Wkb("LINESTRING (0 0, 2 2)")) with
            {
                GeometryTypeChanged = true
            });

        var result = await validator.ValidateGeometryAsync(
            Resource(MetadataV2GeometryType.Polygon), Wkb(CollinearPolygon), CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("from Polygon to LineString")
            .And.Contain("layer geometry type Polygon");
    }

    [UnitTest]
    public async Task ValidateGeometryAsync_RepairYieldsCollectionWithPolygonAndLine_ExtractsPolygon()
    {
        var validator = CreateValidator(
            GeometryValidationResult.SuccessWithRepair(Wkb(
                "GEOMETRYCOLLECTION (POLYGON ((0 0, 1 0, 1 1, 0 1, 0 0)), LINESTRING (1 1, 2 2))")) with
            {
                GeometryTypeChanged = true
            });

        var result = await validator.ValidateGeometryAsync(
            Resource(MetadataV2GeometryType.Polygon), Wkb(CollinearPolygon), CancellationToken.None);

        result.IsValid.Should().BeTrue();
        new WKBReader().Read(result.Geometry!).Should().BeOfType<Polygon>();
    }

    [UnitTest]
    public async Task ValidateGeometryAsync_RepairYieldsCollectionWithTwoPolygons_ExtractsMultiPolygon()
    {
        var validator = CreateValidator(
            GeometryValidationResult.SuccessWithRepair(Wkb(
                "GEOMETRYCOLLECTION (POLYGON ((0 0, 1 0, 1 1, 0 1, 0 0)), POLYGON ((5 5, 6 5, 6 6, 5 6, 5 5)), POINT (9 9))")) with
            {
                GeometryTypeChanged = true
            });

        var result = await validator.ValidateGeometryAsync(
            Resource(MetadataV2GeometryType.MultiPolygon), Wkb(CollinearPolygon), CancellationToken.None);

        result.IsValid.Should().BeTrue();
        new WKBReader().Read(result.Geometry!).Should().BeOfType<MultiPolygon>()
            .Which.NumGeometries.Should().Be(2);
    }

    [UnitTest]
    public async Task ValidateGeometryAsync_RepairYieldsCollectionWithHigherDimensionComponent_IsRejected()
    {
        var validator = CreateValidator(
            GeometryValidationResult.SuccessWithRepair(Wkb(
                "GEOMETRYCOLLECTION (LINESTRING (0 0, 1 1), POLYGON ((0 0, 1 0, 1 1, 0 1, 0 0)))")) with
            {
                GeometryTypeChanged = true
            });

        var result = await validator.ValidateGeometryAsync(
            Resource(MetadataV2GeometryType.LineString), Wkb("LINESTRING (0 0, 0 0)"), CancellationToken.None);

        result.IsValid.Should().BeFalse();
    }

    [UnitTest]
    public async Task ValidateGeometryAsync_PointIntoPolygonLayer_IsRejected()
    {
        var validator = CreateValidator(GeometryValidationResult.Success());

        var result = await validator.ValidateGeometryAsync(
            Resource(MetadataV2GeometryType.Polygon), Wkb("POINT (1 2)"), CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("Geometry type Point does not match the layer geometry type Polygon.");
    }

    [UnitTest]
    public async Task ValidateGeometryAsync_InputGeometryCollectionIntoPolygonLayer_IsRejected()
    {
        var validator = CreateValidator(GeometryValidationResult.Success());

        var result = await validator.ValidateGeometryAsync(
            Resource(MetadataV2GeometryType.Polygon),
            Wkb("GEOMETRYCOLLECTION (POLYGON ((0 0, 1 0, 1 1, 0 1, 0 0)))"),
            CancellationToken.None);

        result.IsValid.Should().BeFalse();
    }

    [UnitTest]
    public async Task ValidateGeometryAsync_MultiFormsOfLayerFamily_AreAccepted()
    {
        var validator = CreateValidator(GeometryValidationResult.Success());

        (await validator.ValidateGeometryAsync(Resource(MetadataV2GeometryType.Polygon),
            Wkb("MULTIPOLYGON (((0 0, 1 0, 1 1, 0 1, 0 0)))"), CancellationToken.None)).IsValid.Should().BeTrue();
        (await validator.ValidateGeometryAsync(Resource(MetadataV2GeometryType.MultiLineString),
            Wkb("LINESTRING (0 0, 1 1)"), CancellationToken.None)).IsValid.Should().BeTrue();
        (await validator.ValidateGeometryAsync(Resource(MetadataV2GeometryType.Point),
            Wkb("MULTIPOINT ((0 0), (1 1))"), CancellationToken.None)).IsValid.Should().BeTrue();
    }

    [UnitTest]
    public async Task ValidateGeometryAsync_UnconstrainedLayers_AcceptAnyGeometryType()
    {
        var validator = CreateValidator(GeometryValidationResult.Success());

        foreach (var layerType in new[]
                 {
                     MetadataV2GeometryType.None,
                     MetadataV2GeometryType.Mixed,
                     MetadataV2GeometryType.GeometryCollection
                 })
        {
            (await validator.ValidateGeometryAsync(Resource(layerType), Wkb("POINT (1 2)"), CancellationToken.None))
                .IsValid.Should().BeTrue(layerType.ToString());
        }

        (await validator.ValidateGeometryAsync(
            new MetadataV2Resource { Metadata = new MetadataV2ObjectMetadata { Id = "no-spatial", Name = "no-spatial" } },
            Wkb("POINT (1 2)"),
            CancellationToken.None)).IsValid.Should().BeTrue();
    }

    private static FeatureMutationValidator CreateValidator(GeometryValidationResult validationResult)
    {
        var geometryValidator = Substitute.For<IGeometryValidator>();
        geometryValidator.ValidateCompleteAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(validationResult));
        return new FeatureMutationValidator(geometryValidator);
    }

    private static MetadataV2Resource Resource(MetadataV2GeometryType geometryType) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "layer", Name = "layer" },
        Spatial = new MetadataV2ResourceSpatial
        {
            SpatialReference = MetadataV2SpatialReference.Wgs84,
            GeometryType = geometryType
        }
    };

    private static byte[] Wkb(string wkt) => new WKBWriter().Write(new WKTReader().Read(wkt));
}
