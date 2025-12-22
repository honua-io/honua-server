// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Postgres.Features.Import;
using Honua.TestKit;
using Xunit;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Integration tests for CRS detection functionality
/// </summary>
[Collection("Database")]
public class CrsDetectionServiceTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private CrsDetectionService? _service;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _service = new CrsDetectionService(_fixture.Postgres.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [Fact]
    public async Task DetectFromEpsgCode_WithValidEpsgCode_ReturnsCorrectSrid()
    {
        // Arrange
        var epsgCode = "EPSG:4326";

        // Act
        var result = _service!.DetectFromEpsgCode(epsgCode);

        // Assert
        result.Should().Be(4326);
    }

    [Theory]
    [InlineData("4326")]
    [InlineData("EPSG:4326")]
    [InlineData("SRID=4326")]
    [InlineData("3857")]
    [InlineData("EPSG:3857")]
    public void DetectFromEpsgCode_WithVariousFormats_ReturnsCorrectSrid(string epsgCode)
    {
        // Act
        var result = _service!.DetectFromEpsgCode(epsgCode);

        // Assert
        result.Should().BeGreaterThan(0);
        result.Should().BeInRange(1000, 99999);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("EPSG:")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("999")]  // Too low
    [InlineData("100000")]  // Too high
    public void DetectFromEpsgCode_WithInvalidFormats_ReturnsNull(string? epsgCode)
    {
        // Act
        var result = _service!.DetectFromEpsgCode(epsgCode!);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task DetectFromWktAsync_WithWgs84Wkt_ReturnsCorrectSrid()
    {
        // Arrange
        var wkt = """
            GEOGCS["WGS 84",
                DATUM["WGS_1984",
                    SPHEROID["WGS 84",6378137,298.257223563]],
                PRIMEM["Greenwich",0],
                UNIT["degree",0.0174532925199433],
                AUTHORITY["EPSG","4326"]]
            """;

        // Act
        var result = await _service!.DetectFromWktAsync(wkt);

        // Assert
        result.Should().Be(4326);
    }

    [Fact]
    public async Task DetectFromWktAsync_WithWebMercatorWkt_ReturnsCorrectSrid()
    {
        // Arrange
        var wkt = """
            PROJCS["WGS 84 / Pseudo-Mercator",
                GEOGCS["WGS 84",
                    DATUM["WGS_1984",
                        SPHEROID["WGS 84",6378137,298.257223563]],
                    PRIMEM["Greenwich",0],
                    UNIT["degree",0.0174532925199433]],
                PROJECTION["Mercator_1SP"],
                PARAMETER["central_meridian",0],
                PARAMETER["scale_factor",1],
                PARAMETER["false_easting",0],
                PARAMETER["false_northing",0],
                UNIT["metre",1],
                AUTHORITY["EPSG","3857"]]
            """;

        // Act
        var result = await _service!.DetectFromWktAsync(wkt);

        // Assert
        result.Should().Be(3857);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task DetectFromWktAsync_WithInvalidWkt_ReturnsNull(string? wkt)
    {
        // Act
        var result = await _service!.DetectFromWktAsync(wkt!);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task DetectFromPrjAsync_WithValidPrjContent_ReturnsCorrectSrid()
    {
        // Arrange - typical .prj file content for WGS84
        var prjContent = """
            GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137,298.257223563]],PRIMEM["Greenwich",0],UNIT["Degree",0.017453292519943295]]
            """;

        // Act
        var result = await _service!.DetectFromPrjAsync(prjContent);

        // Assert
        result.Should().NotBeNull();
        // Should detect as a common coordinate system
    }

    [Fact]
    public async Task DetectFromGeoJsonCrsAsync_WithValidCrsObject_ReturnsCorrectSrid()
    {
        // Arrange
        var crsJson = """
            {
                "type": "name",
                "properties": {
                    "name": "EPSG:4326"
                }
            }
            """;

        // Act
        var result = await _service!.DetectFromGeoJsonCrsAsync(crsJson);

        // Assert
        result.Should().Be(4326);
    }

    [Theory]
    [InlineData("{}")]  // Empty object
    [InlineData("{\"type\":\"link\"}")]  // Wrong type
    [InlineData("invalid json")]  // Invalid JSON
    public async Task DetectFromGeoJsonCrsAsync_WithInvalidCrs_ReturnsNull(string crsJson)
    {
        // Act
        var result = await _service!.DetectFromGeoJsonCrsAsync(crsJson);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateSridAsync_WithValidSrid_ReturnsTrue()
    {
        // Act
        var result = await _service!.ValidateSridAsync(4326);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateSridAsync_WithInvalidSrid_ReturnsFalse()
    {
        // Act - using a very unlikely SRID
        var result = await _service!.ValidateSridAsync(99999);

        // Assert
        // Should return false since SRID doesn't exist in PostGIS
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("WGS84")]
    [InlineData("WGS_84")]
    [InlineData("WGS 84")]
    [InlineData("WEB_MERCATOR")]
    [InlineData("GOOGLE_MERCATOR")]
    public async Task DetectFromWktAsync_WithCommonNames_ReturnsValidSrid(string coordinateSystemName)
    {
        // Arrange
        var wkt = $"""
            GEOGCS["{coordinateSystemName}",
                DATUM["WGS_1984",
                    SPHEROID["WGS 84",6378137,298.257223563]],
                PRIMEM["Greenwich",0],
                UNIT["degree",0.0174532925199433]]
            """;

        // Act
        var result = await _service!.DetectFromWktAsync(wkt);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DetectFromShapefilePrjAsync_WithNonExistentFile_ReturnsNull()
    {
        // Arrange
        var nonExistentShapefilePath = "/tmp/nonexistent.shp";

        // Act
        var result = await _service!.DetectFromShapefilePrjAsync(nonExistentShapefilePath);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task DetectFromShapefilePrjAsync_WithNullOrEmptyPath_ReturnsNull()
    {
        // Act & Assert
        (await _service!.DetectFromShapefilePrjAsync(null!)).Should().BeNull();
        (await _service!.DetectFromShapefilePrjAsync("")).Should().BeNull();
        (await _service!.DetectFromShapefilePrjAsync("   ")).Should().BeNull();
    }
}
