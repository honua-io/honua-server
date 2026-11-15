// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

public partial class CrsDetectionServiceTests
{
    public static IEnumerable<object[]> ProjectedEsriDefinitions()
    {
        yield return [2263, """
            PROJCS["NAD_1983_StatePlane_New_York_Long_Island_FIPS_3104_Feet",GEOGCS["GCS_North_American_1983",DATUM["D_North_American_1983",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Lambert_Conformal_Conic"],PARAMETER["False_Easting",984250.0],PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",-74.0],PARAMETER["Standard_Parallel_1",41.0333333333333],PARAMETER["Standard_Parallel_2",40.6666666666667],PARAMETER["Latitude_Of_Origin",40.1666666666667],UNIT["US survey foot",0.304800609601219]]
            """];
        yield return [6539, """
            PROJCS["NAD_1983_2011_StatePlane_New_York_Long_Isl_FIPS_3104_Ft_US",GEOGCS["GCS_NAD_1983_2011",DATUM["D_NAD_1983_2011",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Lambert_Conformal_Conic"],PARAMETER["False_Easting",984250.0],PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",-74.0],PARAMETER["Standard_Parallel_1",41.0333333333333],PARAMETER["Standard_Parallel_2",40.6666666666667],PARAMETER["Latitude_Of_Origin",40.1666666666667],UNIT["US survey foot",0.304800609601219]]
            """];
        yield return [2229, """
            PROJCS["NAD_1983_StatePlane_California_V_FIPS_0405_Feet",GEOGCS["GCS_North_American_1983",DATUM["D_North_American_1983",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Lambert_Conformal_Conic"],PARAMETER["False_Easting",6561666.667],PARAMETER["False_Northing",1640416.667],PARAMETER["Central_Meridian",-118.0],PARAMETER["Standard_Parallel_1",35.4666666666667],PARAMETER["Standard_Parallel_2",34.0333333333333],PARAMETER["Latitude_Of_Origin",33.5],UNIT["US survey foot",0.304800609601219]]
            """];
        yield return [2154, """
            PROJCS["RGF_1993_Lambert_93",GEOGCS["GCS_RGF_1993",DATUM["D_RGF_1993",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Lambert_Conformal_Conic"],PARAMETER["False_Easting",700000.0],PARAMETER["False_Northing",6600000.0],PARAMETER["Central_Meridian",3.0],PARAMETER["Standard_Parallel_1",49.0],PARAMETER["Standard_Parallel_2",44.0],PARAMETER["Latitude_Of_Origin",46.5],UNIT["Meter",1.0]]
            """];
        yield return [27700, """
            PROJCS["British_National_Grid",GEOGCS["GCS_OSGB_1936",DATUM["D_OSGB_1936",SPHEROID["Airy_1830",6377563.396,299.3249646]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],PARAMETER["False_Easting",400000.0],PARAMETER["False_Northing",-100000.0],PARAMETER["Central_Meridian",-2.0],PARAMETER["Scale_Factor",0.9996012717],PARAMETER["Latitude_Of_Origin",49.0],UNIT["Meter",1.0]]
            """];
        yield return [3005, """
            PROJCS["NAD_1983_BC_Environment_Albers",GEOGCS["GCS_North_American_1983",DATUM["D_North_American_1983",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Albers"],PARAMETER["False_Easting",1000000.0],PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",-126.0],PARAMETER["Standard_Parallel_1",50.0],PARAMETER["Standard_Parallel_2",58.5],PARAMETER["Latitude_Of_Origin",45.0],UNIT["Meter",1.0]]
            """];
        yield return [28355, """
            PROJCS["GDA_1994_MGA_Zone_55",GEOGCS["GCS_GDA_1994",DATUM["D_GDA_1994",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],PARAMETER["False_Easting",500000.0],PARAMETER["False_Northing",10000000.0],PARAMETER["Central_Meridian",147.0],PARAMETER["Scale_Factor",0.9996],PARAMETER["Latitude_Of_Origin",0.0],UNIT["Meter",1.0]]
            """];
        yield return [3577, """
            PROJCS["GDA_1994_Australia_Albers",GEOGCS["GCS_GDA_1994",DATUM["D_GDA_1994",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Albers"],PARAMETER["False_Easting",0.0],PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",132.0],PARAMETER["Standard_Parallel_1",-18.0],PARAMETER["Standard_Parallel_2",-36.0],PARAMETER["Latitude_Of_Origin",0.0],UNIT["Meter",1.0]]
            """];
    }

    [IntegrationTheory]
    [MemberData(nameof(ProjectedEsriDefinitions))]
    public async Task DetectFromPrjAsync_ProjectedEsriWktWithoutAuthority_ReturnsMatchingSrid(int expectedSrid, string wkt)
    {
        wkt.Should().NotContain("AUTHORITY[");

        var result = await _service!.DetectFromPrjAsync(wkt);

        result.Should().Be(expectedSrid);
    }

    [IntegrationTheory]
    [MemberData(nameof(ProjectedEsriDefinitions))]
    public async Task DetectFromPrjAsync_ProjectedNameWithChangedParameters_DoesNotGuessFromName(int originalSrid, string wkt)
    {
        var alteredWkt = wkt.Replace("PARAMETER[\"False_Easting\",", "PARAMETER[\"False_Easting\",1", StringComparison.Ordinal);
        alteredWkt.Should().NotBe(wkt);

        var result = await _service!.DetectFromPrjAsync(alteredWkt);

        result.Should().BeNull($"the parameters no longer describe EPSG:{originalSrid}");
    }

    [IntegrationTest]
    public async Task DetectFromPrjAsync_CustomProjectedLabel_UsesParameters()
    {
        var wkt = ((string)ProjectedEsriDefinitions().First()[1]).Replace(
            "NAD_1983_StatePlane_New_York_Long_Island_FIPS_3104_Feet", "Surveyor_Custom_Label", StringComparison.Ordinal);

        var result = await _service!.DetectFromPrjAsync(wkt);

        result.Should().Be(2263);
    }

    [IntegrationTheory]
    [InlineData("US survey foot\",0.304800609601219", "Foot\",0.3048")]
    [InlineData("D_North_American_1983", "D_Unknown_Datum")]
    [InlineData("UNIT[\"US survey foot\",0.304800609601219]", "UNIT[\"US survey foot\",0.304800609601219],AXIS[\"Northing\",NORTH],AXIS[\"Easting\",EAST]")]
    [InlineData("SPHEROID[\"GRS_1980\",6378137.0,298.257222101]", "SPHEROID[\"GRS_1980\",6378137.0,298.257222101],TOWGS84[100,0,0,0,0,0,0]")]
    [InlineData("298.257222101", "298.257223563")]
    [InlineData("Greenwich\",0.0", "Greenwich\",2.0")]
    [InlineData("PARAMETER[\"Central_Meridian\",-74.0]", "PARAMETER[\"Central_Meridian\",-74.0],PARAMETER[\"Central_Meridian\",-75.0]")]
    [InlineData("PROJECTION[\"Lambert_Conformal_Conic\"]", "PROJECTION[\"Lambert_Conformal_Conic\"],EXTENSION[\"custom\",\"unrecognized\"]")]
    public async Task DetectFromPrjAsync_ConflictingProjectedDefinition_ReturnsNull(string original, string replacement)
    {
        var wkt = (string)ProjectedEsriDefinitions().First()[1];
        var altered = wkt.Replace(original, replacement, StringComparison.Ordinal);
        altered.Should().NotBe(wkt);

        var result = await _service!.DetectFromPrjAsync(altered);

        result.Should().BeNull();
    }

    [IntegrationTheory]
    [InlineData("trailing text")]
    [InlineData(",EXTENSION[\"custom\",\"unrecognized\"]")]
    public async Task DetectFromPrjAsync_MalformedProjectedDefinition_ReturnsNull(string suffix)
    {
        var wkt = (string)ProjectedEsriDefinitions().First()[1];
        var result = await _service!.DetectFromPrjAsync(wkt + suffix);

        result.Should().BeNull();
    }

    [IntegrationTest]
    public async Task DetectFromPrjAsync_AmbiguousProjectedRegistryMatch_ReturnsNull()
    {
        await using var connection = await _fixture.GetConnectionAsync();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO spatial_ref_sys (srid, auth_name, auth_srid, srtext, proj4text)
            SELECT 990226, 'EPSG', 990226, srtext, proj4text
            FROM spatial_ref_sys WHERE srid = 2263
            """, connection);
        await insert.ExecuteNonQueryAsync();
        try
        {
            var result = await _service!.DetectFromPrjAsync((string)ProjectedEsriDefinitions().First()[1]);

            result.Should().BeNull("multiple registry entries match the same parameters");
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand("DELETE FROM spatial_ref_sys WHERE srid = 990226", connection);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
