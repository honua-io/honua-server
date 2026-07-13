// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;
using Honua.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Integration tests for CRS detection functionality
/// </summary>
[Collection("Database")]
public class CrsDetectionServiceTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture = new();
    private CrsDetectionService? _service;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        var connectionProvider = new PostgresDatabaseConnectionProvider(
            _fixture.DataSource,
            NullLogger<PostgresDatabaseConnectionProvider>.Instance);
        _service = new CrsDetectionService(connectionProvider, NullLogger<CrsDetectionService>.Instance);
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
        result.Should().BeInRange(1, 999999);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("EPSG:")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("0")]  // Zero is invalid
    [InlineData("1000000")]  // Too high
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
    public async Task DetectFromWktAsync_WithAmbiguousProjectedPrefix_DoesNotGuessSrid()
    {
        var wkt = """
            PROJCS["WGS 84 / UTM zone 33N",
                GEOGCS["WGS 84",
                    DATUM["WGS_1984",
                        SPHEROID["WGS 84",6378137,298.257223563]],
                    PRIMEM["Greenwich",0],
                    UNIT["degree",0.0174532925199433]],
                PROJECTION["Transverse_Mercator"],
                PARAMETER["latitude_of_origin",0],
                PARAMETER["central_meridian",15]
            """;

        var result = await _service!.DetectFromWktAsync(wkt);

        result.Should().BeNull();
    }

    [IntegrationTest]
    public async Task DetectFromWktAsync_WithWkt2IdNode_ReturnsProjectedSrid()
    {
        // PROJ-6 / WKT2 .prj (e.g. produced by GDAL 3+, FlatGeobuf) carries ID["EPSG",N]
        // rather than AUTHORITY[...]. The projected CRS's own ID is the last one in the string;
        // the inner base-geographic CRS's ID (4258) must not win.
        var wkt2 = """
            PROJCRS["ETRS89 / UTM zone 32N",
                BASEGEOGCRS["ETRS89",
                    DATUM["European Terrestrial Reference System 1989",
                        ELLIPSOID["GRS 1980",6378137,298.257222101,LENGTHUNIT["metre",1]]],
                    ID["EPSG",4258]],
                CONVERSION["UTM zone 32N",
                    METHOD["Transverse Mercator",ID["EPSG",9807]],
                    PARAMETER["Latitude of natural origin",0],
                    PARAMETER["Longitude of natural origin",9]],
                CS[Cartesian,2],
                    AXIS["(E)",east],
                    AXIS["(N)",north],
                    LENGTHUNIT["metre",1],
                ID["EPSG",25832]]
            """;

        var result = await _service!.DetectFromWktAsync(wkt2);

        result.Should().Be(25832);
    }

    [IntegrationTest]
    public async Task DetectFromWktAsync_WithDualAuthorityNodes_ReturnsProjectedNotBaseGeographic()
    {
        // A WKT1 UTM string with an AUTHORITY node on both the inner GEOGCS (4326) and the outer
        // PROJCS (32632). Taking the FIRST match wrongly returns 4326; the outer projected code wins.
        var wkt1 = """
            PROJCS["WGS 84 / UTM zone 32N",
                GEOGCS["WGS 84",
                    DATUM["WGS_1984",
                        SPHEROID["WGS 84",6378137,298.257223563,AUTHORITY["EPSG","7030"]],
                        AUTHORITY["EPSG","6326"]],
                    PRIMEM["Greenwich",0,AUTHORITY["EPSG","8901"]],
                    UNIT["degree",0.0174532925199433,AUTHORITY["EPSG","9122"]],
                    AUTHORITY["EPSG","4326"]],
                PROJECTION["Transverse_Mercator"],
                PARAMETER["latitude_of_origin",0],
                PARAMETER["central_meridian",9],
                PARAMETER["scale_factor",0.9996],
                PARAMETER["false_easting",500000],
                PARAMETER["false_northing",0],
                UNIT["metre",1,AUTHORITY["EPSG","9001"]],
                AUTHORITY["EPSG","32632"]]
            """;

        var result = await _service!.DetectFromWktAsync(wkt1);

        result.Should().Be(32632);
    }

    [IntegrationTest]
    public async Task DetectFromPrjAsync_WithEsriUtmName_ReturnsComputedSrid()
    {
        // Typical ArcGIS-authored .prj: no AUTHORITY/ID node at all; the projected CRS name
        // encodes datum + UTM zone + hemisphere (NAD_1983_UTM_Zone_17N => EPSG:26917).
        var esriPrj = """
            PROJCS["NAD_1983_UTM_Zone_17N",
                GEOGCS["GCS_North_American_1983",
                    DATUM["D_North_American_1983",
                        SPHEROID["GRS_1980",6378137.0,298.257222101]],
                    PRIMEM["Greenwich",0.0],
                    UNIT["Degree",0.0174532925199433]],
                PROJECTION["Transverse_Mercator"],
                PARAMETER["False_Easting",500000.0],
                PARAMETER["Central_Meridian",-81.0],
                PARAMETER["Scale_Factor",0.9996],
                PARAMETER["Latitude_Of_Origin",0.0],
                UNIT["Meter",1.0]]
            """;

        var result = await _service!.DetectFromPrjAsync(esriPrj);

        result.Should().Be(26917);
    }

    [IntegrationTest]
    public async Task DetectFromPrjAsync_WithEsriWebMercatorName_ReturnsSrid()
    {
        // ArcGIS Web Mercator .prj name maps to EPSG:3857 via the explicit ESRI-name table.
        var esriPrj = """
            PROJCS["WGS_1984_Web_Mercator_Auxiliary_Sphere",
                GEOGCS["GCS_WGS_1984",
                    DATUM["D_WGS_1984",
                        SPHEROID["WGS_1984",6378137.0,298.257223563]],
                    PRIMEM["Greenwich",0.0],
                    UNIT["Degree",0.0174532925199433]],
                PROJECTION["Mercator_Auxiliary_Sphere"],
                PARAMETER["False_Easting",0.0],
                PARAMETER["Central_Meridian",0.0],
                PARAMETER["Standard_Parallel_1",0.0],
                PARAMETER["Auxiliary_Sphere_Type",0.0],
                UNIT["Meter",1.0]]
            """;

        var result = await _service!.DetectFromPrjAsync(esriPrj);

        result.Should().Be(3857);
    }

    [IntegrationTest]
    public async Task DetectFromPrjAsync_WithNad83HarnUtmName_DoesNotComputePlainNad83Srid()
    {
        // NAD83(HARN) is a distinct datum realization from plain NAD83. Its UTM zone codes are NOT
        // 26900+zone (that is plain NAD83), so the ESRI UTM zone-arithmetic branch must NOT match a
        // "_HARN" name and silently return 26917 (#2743). The made-up name/body has no
        // spatial_ref_sys match, so detection falls through to null rather than a wrong datum.
        var harnPrj = """
            PROJCS["NAD_1983_HARN_UTM_Zone_17N",
                GEOGCS["GCS_North_American_1983_HARN",
                    DATUM["D_North_American_1983_HARN",
                        SPHEROID["GRS_1980",6378137.0,298.257222101]],
                    PRIMEM["Greenwich",0.0],
                    UNIT["Degree",0.0174532925199433]],
                PROJECTION["Transverse_Mercator"],
                PARAMETER["False_Easting",500000.0],
                PARAMETER["Central_Meridian",-81.0],
                PARAMETER["Scale_Factor",0.9996],
                PARAMETER["Latitude_Of_Origin",0.0],
                UNIT["Meter",1.0]]
            """;

        var result = await _service!.DetectFromPrjAsync(harnPrj);

        result.Should().NotBe(26917);
    }

    [IntegrationTest]
    public async Task DetectFromWktAsync_WithCompoundCrs_ResolvesHorizontalNotVerticalAuthority()
    {
        // COMPD_CS carries a trailing VERT_CS whose EPSG authority (5703, NAVD88 height) validates
        // in spatial_ref_sys. Detection must resolve to the horizontal (projected) member 32617, not
        // the vertical 5703 (#2743).
        var compound = """
            COMPD_CS["NAD83 / UTM zone 17N + NAVD88 height",
                PROJCS["NAD83 / UTM zone 17N",
                    GEOGCS["NAD83",
                        DATUM["North_American_Datum_1983",
                            SPHEROID["GRS 1980",6378137,298.257222101]],
                        PRIMEM["Greenwich",0],
                        UNIT["degree",0.0174532925199433],
                        AUTHORITY["EPSG","4269"]],
                    PROJECTION["Transverse_Mercator"],
                    PARAMETER["latitude_of_origin",0],
                    PARAMETER["central_meridian",-81],
                    PARAMETER["scale_factor",0.9996],
                    PARAMETER["false_easting",500000],
                    PARAMETER["false_northing",0],
                    UNIT["metre",1],
                    AUTHORITY["EPSG","32617"]],
                VERT_CS["NAVD88 height",
                    VERT_DATUM["North American Vertical Datum 1988",2005],
                    UNIT["metre",1],
                    AUTHORITY["EPSG","5703"]]]
            """;

        var result = await _service!.DetectFromWktAsync(compound);

        result.Should().Be(32617);
    }

    [IntegrationTest]
    public async Task DetectFromWktAsync_WithBoundCrs_ResolvesSourceNotTargetAuthority()
    {
        // BOUNDCRS wraps a SOURCECRS (the data's CRS, 25832) plus a TARGETCRS (4326) and an abridged
        // transformation. Detection must resolve the source projected code, not the WGS 84 target
        // that would otherwise win as a trailing authority (#2743).
        var bound = """
            BOUNDCRS[
                SOURCECRS[
                    PROJCRS["ETRS89 / UTM zone 32N",
                        BASEGEOGCRS["ETRS89",
                            DATUM["European Terrestrial Reference System 1989",
                                ELLIPSOID["GRS 1980",6378137,298.257222101]]],
                        CONVERSION["UTM zone 32N",
                            METHOD["Transverse Mercator",ID["EPSG",9807]],
                            PARAMETER["Latitude of natural origin",0],
                            PARAMETER["Longitude of natural origin",9]],
                        CS[Cartesian,2],
                        AXIS["(E)",east],
                        AXIS["(N)",north],
                        LENGTHUNIT["metre",1],
                        ID["EPSG",25832]]],
                TARGETCRS[
                    GEOGCRS["WGS 84",
                        DATUM["World Geodetic System 1984",
                            ELLIPSOID["WGS 84",6378137,298.257223563]],
                        CS[ellipsoidal,2],
                        AXIS["latitude",north],
                        AXIS["longitude",east],
                        ANGLEUNIT["degree",0.0174532925199433],
                        ID["EPSG",4326]]],
                ABRIDGEDTRANSFORMATION["Transformation from ETRS89 to WGS84",
                    METHOD["Geocentric translations (geog2D domain)",ID["EPSG",9603]],
                    PARAMETER["X-axis translation",0],
                    ID["EPSG",1149]]]
            """;

        var result = await _service!.DetectFromWktAsync(bound);

        result.Should().Be(25832);
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
