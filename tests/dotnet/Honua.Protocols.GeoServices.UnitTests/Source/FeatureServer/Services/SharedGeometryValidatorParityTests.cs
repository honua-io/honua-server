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
        var validator = CreateValidator(maxVertices: 5, maxRings: 1);

        var pointZ = new WKTReader().Read("POINT Z (1 2 3)");
        pointZ.SRID = 4326;
        var pointResult = validator.ValidateWkb(new WKBWriter(ByteOrder.LittleEndian, handleSRID: true, emitZ: true).Write(pointZ));
        pointResult.IsValid.Should().BeTrue();
        pointResult.Stats.Should().BeEquivalentTo(new
        {
            GeometryType = "Point",
            VertexCount = 1,
            RingCount = 0,
            HasZ = true,
            HasM = false,
            Srid = (int?)4326
        });

        AssertValidationError(validator, "LINESTRING EMPTY", ValidationErrorCode.EmptyGeometry);
        AssertValidationError(validator, "LINESTRING (0 0, 1 1, 2 2, 3 3, 4 4, 5 5)", ValidationErrorCode.TooManyVertices);

        const string donutWkt = "POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0), (2 2, 4 2, 4 4, 2 4, 2 2))";
        AssertValidationError(validator, donutWkt, ValidationErrorCode.TooManyVertices);

        var ringLimitedValidator = CreateValidator(maxVertices: 100, maxRings: 1);
        AssertValidationError(ringLimitedValidator, donutWkt, ValidationErrorCode.TooManyRings);

        var collectionWkb = new WKBWriter().Write(
            new WKTReader().Read("GEOMETRYCOLLECTION (POINT (1 2), POLYGON ((0 0, 1 0, 1 1, 0 0)))"));
        var collectionResult = ringLimitedValidator.ValidateWkb(collectionWkb);
        collectionResult.IsValid.Should().BeTrue();
        collectionResult.Stats!.GeometryType.Should().Be("GeometryCollection");
        collectionResult.Stats.VertexCount.Should().Be(5);
        collectionResult.Stats.RingCount.Should().Be(1);
    }

    private static WkbGeometryValidator CreateValidator(int maxVertices, int maxRings) => new(
        Options.Create(new LimitsOptions
        {
            Validation = new GeometryValidationOptions { MaxVertices = maxVertices, MaxRings = maxRings }
        }),
        Substitute.For<IGeometryTopologyValidator>(),
        NullLogger.Instance);

    private static void AssertValidationError(
        WkbGeometryValidator validator,
        string wkt,
        ValidationErrorCode expectedCode)
    {
        var result = validator.ValidateWkb(new WKBWriter().Write(new WKTReader().Read(wkt)));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(expectedCode);
    }
}
