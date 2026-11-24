// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Core.Configuration;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;
using Honua.Infrastructure.Validation;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class SharedGeometryValidatorParityTests
{
    [UnitTheory]
    [InlineData("POINT (1 2)")]
    [InlineData("LINESTRING (0 0, 1 1, 2 2)")]
    [InlineData("POLYGON ((0 0, 0 1, 1 1, 0 0))")]
    public async Task SharedWkbPipelineMatchesGeoServicesAdapter(string wkt)
    {
        var options = Options.Create(new LimitsOptions());
        var topology = Substitute.For<IGeometryTopologyValidator>();
        topology.ValidateTopologyAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(GeometryValidationResult.Success());
        var adapter = new GeometryValidator(options, topology, NullLogger<GeometryValidator>.Instance);
        var shared = new WkbGeometryValidator(options, topology, NullLogger.Instance);
        var bytes = new WKBWriter().Write(new WKTReader().Read(wkt));

        shared.ValidateWkb(bytes).IsValid.Should().BeTrue();
        shared.ValidateWkb([1, 2, 3]).IsValid.Should().BeFalse();
        shared.ValidateWkb([1, 2, 3]).Errors.Should().ContainSingle()
            .Which.Code.Should().Be(ValidationErrorCode.InvalidWkbStructure);
        adapter.ValidateWkb(bytes).Should().BeEquivalentTo(shared.ValidateWkb(bytes));
        (await adapter.ValidateCompleteAsync(bytes)).Should().BeEquivalentTo(await shared.ValidateCompleteAsync(bytes));
        adapter.ValidateWkb([1, 2, 3]).Should().BeEquivalentTo(shared.ValidateWkb([1, 2, 3]));
        adapter.ValidateWkb(null).Should().BeEquivalentTo(shared.ValidateWkb(null));
    }

    [UnitTest]
    public void ValidateWkb_ExtendedDimensionsSridAndLimits_ProducesExpectedStatsAndErrors()
    {
        var options = Options.Create(new LimitsOptions
        {
            Validation = new GeometryValidationOptions
            {
                MaxVertices = 5,
                MaxRings = 1
            }
        });
        var topology = Substitute.For<IGeometryTopologyValidator>();
        var validator = new WkbGeometryValidator(options, topology, NullLogger.Instance);

        var pointZ = new WKTReader().Read("POINT Z (1 2 3)");
        pointZ.SRID = 4326;
        var pointZWkb = new WKBWriter(ByteOrder.LittleEndian, handleSRID: true, emitZ: true).Write(pointZ);
        var pointResult = validator.ValidateWkb(pointZWkb);
        pointResult.IsValid.Should().BeTrue();
        pointResult.Stats.Should().NotBeNull();
        pointResult.Stats!.GeometryType.Should().Be("Point");
        pointResult.Stats.VertexCount.Should().Be(1);
        pointResult.Stats.RingCount.Should().Be(0);
        pointResult.Stats.HasZ.Should().BeTrue();
        pointResult.Stats.HasM.Should().BeFalse();
        pointResult.Stats.Srid.Should().Be(4326);

        var emptyWkb = new WKBWriter().Write(new WKTReader().Read("LINESTRING EMPTY"));
        var emptyResult = validator.ValidateWkb(emptyWkb);
        emptyResult.IsValid.Should().BeFalse();
        emptyResult.Errors.Should().ContainSingle().Which.Code.Should().Be(ValidationErrorCode.EmptyGeometry);

        var tooManyVerticesWkb = new WKBWriter().Write(
            new WKTReader().Read("LINESTRING (0 0, 1 1, 2 2, 3 3, 4 4, 5 5)"));
        var verticesResult = validator.ValidateWkb(tooManyVerticesWkb);
        verticesResult.IsValid.Should().BeFalse();
        verticesResult.Errors.Should().ContainSingle().Which.Code.Should().Be(ValidationErrorCode.TooManyVertices);

        var donutWkb = new WKBWriter().Write(
            new WKTReader().Read("POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0), (2 2, 4 2, 4 4, 2 4, 2 2))"));
        var ringsResult = validator.ValidateWkb(donutWkb);
        ringsResult.IsValid.Should().BeFalse();
        ringsResult.Errors.Should().ContainSingle().Which.Code.Should().Be(ValidationErrorCode.TooManyVertices);

        var ringLimitedValidator = new WkbGeometryValidator(
            Options.Create(new LimitsOptions
            {
                Validation = new GeometryValidationOptions { MaxVertices = 100, MaxRings = 1 }
            }),
            topology,
            NullLogger.Instance);
        var ringLimitResult = ringLimitedValidator.ValidateWkb(donutWkb);
        ringLimitResult.IsValid.Should().BeFalse();
        ringLimitResult.Errors.Should().ContainSingle().Which.Code.Should().Be(ValidationErrorCode.TooManyRings);

        var collectionWkb = new WKBWriter().Write(
            new WKTReader().Read("GEOMETRYCOLLECTION (POINT (1 2), POLYGON ((0 0, 1 0, 1 1, 0 0)))"));
        var collectionResult = ringLimitedValidator.ValidateWkb(collectionWkb);
        collectionResult.IsValid.Should().BeTrue();
        collectionResult.Stats!.GeometryType.Should().Be("GeometryCollection");
        collectionResult.Stats.VertexCount.Should().Be(5);
        collectionResult.Stats.RingCount.Should().Be(1);
    }
}
