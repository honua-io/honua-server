// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Db.Postgres.Features.FileImport;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed class EsriProjectedCrsMatcherTests
{
    private const string NewYorkLongIsland = """
        PROJCS["NAD_1983_StatePlane_New_York_Long_Island_FIPS_3104_Feet",
            GEOGCS["GCS_North_American_1983",
                DATUM["D_North_American_1983",
                    SPHEROID["GRS_1980",6378137.0,298.257222101]],
                PRIMEM["Greenwich",0.0],
                UNIT["Degree",0.0174532925199433]],
            PROJECTION["Lambert_Conformal_Conic"],
            PARAMETER["False_Easting",984250.0],
            PARAMETER["False_Northing",0.0],
            PARAMETER["Central_Meridian",-74.0],
            PARAMETER["Standard_Parallel_1",40.66666666666666],
            PARAMETER["Standard_Parallel_2",41.03333333333333],
            PARAMETER["Latitude_Of_Origin",40.16666666666666],
            UNIT["Foot_US",0.3048006096012192]]
        """;

    [UnitTest]
    public void Select_PrefersPlainNad83OverThe2011Realization()
    {
        EsriProjectedCrsMatcher.TryParse(NewYorkLongIsland, out var signature).Should().BeTrue();

        var selected = EsriProjectedCrsMatcher.Select(signature,
        [
            new(6539, "NAD83(2011) / New York Long Island (ftUS)", ClassicFeet(300000)),
            new(2263, "NAD83 / New York Long Island (ftUS)", ClassicFeet(300000)),
        ]);

        selected.Should().Be(2263);
    }

    [UnitTest]
    public void Select_Uses2011WhenThePrjNameSaysSo()
    {
        var wkt = NewYorkLongIsland.Replace(
            "NAD_1983_StatePlane",
            "NAD_1983_2011_StatePlane",
            StringComparison.Ordinal);
        EsriProjectedCrsMatcher.TryParse(wkt, out var signature).Should().BeTrue();

        var selected = EsriProjectedCrsMatcher.Select(signature,
        [
            new(2263, "NAD83 / New York Long Island (ftUS)", ClassicFeet(300000)),
            new(6539, "NAD83(2011) / New York Long Island (ftUS)", ClassicFeet(300000)),
        ]);

        selected.Should().Be(6539);
    }

    [UnitTest]
    public void Select_MatchesModernUsFootOffsetsAndUtmShorthand()
    {
        EsriProjectedCrsMatcher.TryParse(NewYorkLongIsland, out var statePlane).Should().BeTrue();
        var feet = EsriProjectedCrsMatcher.Select(statePlane,
        [
            new(32118, "NAD83 / New York Long Island",
                "+proj=lcc +lat_0=40.1666666666667 +lon_0=-74 +lat_1=40.6666666666667 +lat_2=41.0333333333333 +x_0=300000 +y_0=0 +datum=NAD83 +units=m +no_defs"),
            new(2263, "NAD83 / New York Long Island (ftUS)",
                "+proj=lcc +lat_0=40.1666666666667 +lon_0=-74 +lat_1=40.6666666666667 +lat_2=41.0333333333333 +x_0=300000 +y_0=0 +datum=NAD83 +units=us-ft +no_defs"),
        ]);
        feet.Should().Be(2263);

        const string mga = """
            PROJCS["GDA_1994_MGA_Zone_55",
                GEOGCS["GCS_GDA_1994",
                    DATUM["D_GDA_1994", SPHEROID["GRS_1980",6378137.0,298.257222101]],
                    PRIMEM["Greenwich",0.0], UNIT["Degree",0.0174532925199433]],
                PROJECTION["Transverse_Mercator"],
                PARAMETER["False_Easting",500000.0],
                PARAMETER["False_Northing",10000000.0],
                PARAMETER["Central_Meridian",147.0],
                PARAMETER["Scale_Factor",0.9996],
                PARAMETER["Latitude_Of_Origin",0.0],
                UNIT["Meter",1.0]]
            """;
        EsriProjectedCrsMatcher.TryParse(mga, out var zone).Should().BeTrue();
        var selected = EsriProjectedCrsMatcher.Select(zone,
        [
            new(28355, "GDA94 / MGA zone 55", "+proj=utm +zone=55 +south +ellps=GRS80 +units=m +no_defs"),
            new(28354, "GDA94 / MGA zone 54", "+proj=utm +zone=54 +south +ellps=GRS80 +units=m +no_defs"),
        ]);
        selected.Should().Be(28355);
    }

    [UnitTest]
    public void Select_ReturnsNullWhenParametersMatchMoreThanOneDatum()
    {
        var wkt = """
            PROJCS["Local_Grid",
                GEOGCS["GCS_Local",
                    DATUM["D_Local", SPHEROID["GRS_1980",6378137.0,298.257222101]],
                    PRIMEM["Greenwich",0.0], UNIT["Degree",0.0174532925199433]],
                PROJECTION["Lambert_Conformal_Conic"],
                PARAMETER["False_Easting",984250.0], PARAMETER["False_Northing",0.0],
                PARAMETER["Central_Meridian",-74.0],
                PARAMETER["Standard_Parallel_1",40.66666666666666],
                PARAMETER["Standard_Parallel_2",41.03333333333333],
                PARAMETER["Latitude_Of_Origin",40.16666666666666],
                UNIT["Foot_US",0.3048006096012192]]
            """;
        EsriProjectedCrsMatcher.TryParse(wkt, out var signature).Should().BeTrue();

        var selected = EsriProjectedCrsMatcher.Select(signature,
        [
            new(2263, "NAD83 / New York Long Island (ftUS)", ClassicFeet(300000)),
            new(6539, "NAD83(2011) / New York Long Island (ftUS)", ClassicFeet(300000)),
        ]);

        selected.Should().BeNull();
    }

    [UnitTest]
    public void Select_MatchesTransverseMercatorScaleAndSwappedLambertParallels()
    {
        const string bng = """
            PROJCS["British_National_Grid",
                GEOGCS["GCS_OSGB_1936",
                    DATUM["D_OSGB_1936", SPHEROID["Airy_1830",6377563.396,299.3249646]],
                    PRIMEM["Greenwich",0.0], UNIT["Degree",0.0174532925199433]],
                PROJECTION["Transverse_Mercator"],
                PARAMETER["False_Easting",400000.0], PARAMETER["False_Northing",-100000.0],
                PARAMETER["Central_Meridian",-2.0], PARAMETER["Scale_Factor",0.9996012717],
                PARAMETER["Latitude_Of_Origin",49.0], UNIT["Meter",1.0]]
            """;
        EsriProjectedCrsMatcher.TryParse(bng, out var grid).Should().BeTrue();
        grid.Lat1.Should().BeNull();
        EsriProjectedCrsMatcher.Select(grid,
        [
            new(7405, "COMPD_CS[\"OSGB36 / British National Grid + ODN height\"]",
                "+proj=tmerc +lat_0=49 +lon_0=-2 +k=0.9996012717 +x_0=400000 +y_0=-100000 +ellps=airy +units=m +vunits=m +no_defs"),
            new(27700, "OSGB 1936 / British National Grid",
                "+proj=tmerc +lat_0=49 +lon_0=-2 +k_0=0.9996012717 +x_0=400000 +y_0=-100000 +ellps=airy +datum=OSGB36 +units=m +no_defs"),
        ]).Should().Be(27700);

        EsriProjectedCrsMatcher.TryParse(NewYorkLongIsland, out var statePlane).Should().BeTrue();
        EsriProjectedCrsMatcher.Select(statePlane,
        [
            new(2263, "NAD83 / New York Long Island (ftUS)",
                "+proj=lcc +lat_0=40.16666666666666 +lon_0=-74 +lat_1=41.03333333333333 +lat_2=40.66666666666666 +x_0=300000 +y_0=0 +datum=NAD83 +to_meter=0.3048006096012192 +no_defs"),
        ]).Should().Be(2263);
    }

    private static string ClassicFeet(int eastingMeters)
        => $"+proj=lcc +lat_0=40.16666666666666 +lon_0=-74 +lat_1=40.66666666666666 +lat_2=41.03333333333333 +x_0={eastingMeters} +y_0=0 +ellps=GRS80 +datum=NAD83 +to_meter=0.3048006096012192 +no_defs";
}
