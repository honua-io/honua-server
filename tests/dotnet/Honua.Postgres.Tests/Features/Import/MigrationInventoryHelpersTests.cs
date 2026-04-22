// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Import;
using Moq;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class MigrationInventoryHelpersTests
{
    [Fact]
    public async Task BuildSpatialReferenceAsync_WithWkt1Datum_ReturnsDatumNodeValue()
    {
        var spatialReference = await MigrationInventoryHelpers.BuildSpatialReferenceAsync(
            CreateCrsRegistry().Object,
            "declared",
            """
            PROJCS["WGS 84 / Pseudo-Mercator",GEOGCS["WGS 84",DATUM["WGS_1984",SPHEROID["WGS 84",6378137,298.257223563]],PRIMEM["Greenwich",0],UNIT["degree",0.0174532925199433]],PROJECTION["Mercator_1SP"],UNIT["metre",1]]
            """,
            CancellationToken.None);

        spatialReference.Should().NotBeNull();
        spatialReference!.Datum.Should().Be("WGS_1984");
    }

    [Fact]
    public async Task BuildSpatialReferenceAsync_WithWkt2Datum_ReturnsDatumNodeValue()
    {
        var spatialReference = await MigrationInventoryHelpers.BuildSpatialReferenceAsync(
            CreateCrsRegistry().Object,
            "declared",
            """
            PROJCRS["WGS 84 / Pseudo-Mercator",BASEGEOGCRS["WGS 84",DATUM["World Geodetic System 1984",ELLIPSOID["WGS 84",6378137,298.257223563]]],CONVERSION["Popular Visualisation Pseudo-Mercator"],CS[Cartesian,2],AXIS["easting",east],AXIS["northing",north],LENGTHUNIT["metre",1]]
            """,
            CancellationToken.None);

        spatialReference.Should().NotBeNull();
        spatialReference!.Datum.Should().Be("World Geodetic System 1984");
    }

    private static Mock<ICrsRegistry> CreateCrsRegistry()
    {
        var registry = new Mock<ICrsRegistry>(MockBehavior.Strict);
        registry.Setup(service => service.ResolveBySridAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<CrsDefinition?>((CrsDefinition?)null));
        return registry;
    }
}
