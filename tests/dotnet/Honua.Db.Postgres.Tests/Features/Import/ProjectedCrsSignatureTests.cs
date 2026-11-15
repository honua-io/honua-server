// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.FileImport;

namespace Honua.Db.Postgres.Tests.Features.Import;

[Trait("Category", "Unit")]
[Trait("Tier", "Fast")]
public sealed class ProjectedCrsSignatureTests
{
    // EPSG:2263 as PostGIS spatial_ref_sys stores it (OGC names, parallels in the other order, authority nodes).
    private const string Epsg2263SrText =
        "PROJCS[\"NAD83 / New York Long Island (ftUS)\",GEOGCS[\"NAD83\",DATUM[\"North_American_Datum_1983\"," +
        "SPHEROID[\"GRS 1980\",6378137,298.257222101,AUTHORITY[\"EPSG\",\"7019\"]],TOWGS84[0,0,0,0,0,0,0],AUTHORITY[\"EPSG\",\"6269\"]]," +
        "PRIMEM[\"Greenwich\",0,AUTHORITY[\"EPSG\",\"8901\"]],UNIT[\"degree\",0.0174532925199433,AUTHORITY[\"EPSG\",\"9122\"]]," +
        "AUTHORITY[\"EPSG\",\"4269\"]],PROJECTION[\"Lambert_Conformal_Conic_2SP\"],PARAMETER[\"standard_parallel_1\",41.03333333333333]," +
        "PARAMETER[\"standard_parallel_2\",40.66666666666666],PARAMETER[\"latitude_of_origin\",40.16666666666666]," +
        "PARAMETER[\"central_meridian\",-74],PARAMETER[\"false_easting\",984250.0000000002],PARAMETER[\"false_northing\",0]," +
        "UNIT[\"US survey foot\",0.3048006096012192,AUTHORITY[\"EPSG\",\"9003\"]],AXIS[\"Easting\",EAST],AXIS[\"Northing\",NORTH]," +
        "AUTHORITY[\"EPSG\",\"2263\"]]";

    [Fact]
    public void EsriStatePlane_MatchesItsEpsgDefinition()
    {
        var esri = ProjectedCrsSignature.TryCreate(EsriProjectedPrj.NewYorkLongIslandFtUs);
        var epsg = ProjectedCrsSignature.TryCreate(Epsg2263SrText);

        esri.Should().NotBeNull();
        epsg.Should().NotBeNull();
        esri!.Matches(epsg!).Should().BeTrue();
        esri.Datum.EpsgCode.Should().Be("6269");
    }

    [Theory]
    [InlineData(nameof(EsriProjectedPrj.NewYorkLongIsland2011FtUs))]
    [InlineData(nameof(EsriProjectedPrj.NewYorkLongIslandInternationalFeet))]
    public void SameParametersOnAnotherDatumOrUnit_DoNotMatch(string fixture)
    {
        var other = ProjectedCrsSignature.TryCreate(Fixture(fixture));
        var epsg = ProjectedCrsSignature.TryCreate(Epsg2263SrText);

        other.Should().NotBeNull();
        other!.Matches(epsg!).Should().BeFalse();
    }

    [Fact]
    public void UnrecognisedDatum_NeverMatches_EvenWithIdenticalParameters()
    {
        var unknown = ProjectedCrsSignature.TryCreate(EsriProjectedPrj.MgaZone55UnknownDatum);
        var gda94 = ProjectedCrsSignature.TryCreate(EsriProjectedPrj.MgaZone55);

        unknown!.Datum.EpsgCode.Should().BeNull();
        unknown.Matches(gda94!).Should().BeFalse();
        unknown.Matches(unknown).Should().BeFalse("an unknown datum cannot be verified, not even against itself");
    }

    [Fact]
    public void ShiftedFalseEasting_DoesNotMatch()
    {
        var shifted = ProjectedCrsSignature.TryCreate(
            EsriProjectedPrj.NewYorkLongIslandFtUs.Replace("984250.0", "984260.0", StringComparison.Ordinal));

        shifted!.Matches(ProjectedCrsSignature.TryCreate(Epsg2263SrText)!).Should().BeFalse();
    }

    [Theory]
    [InlineData("GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\",SPHEROID[\"WGS_1984\",6378137.0,298.257223563]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]")]
    [InlineData("not wkt")]
    [InlineData("PROJCS[\"x\",GEOGCS[\"g\",DATUM[\"D_WGS_1984\",SPHEROID[\"s\",6378137.0,298.257223563]]],PROJECTION[\"Hotine_Oblique_Mercator_Azimuth_Natural_Origin\"],UNIT[\"Meter\",1.0]]")]
    public void UnsupportedOrGeographicWkt_HasNoSignature(string wkt) =>
        ProjectedCrsSignature.TryCreate(wkt).Should().BeNull();

    [Fact]
    public void UnknownProjectionParameter_HasNoSignature() =>
        ProjectedCrsSignature.TryCreate(EsriProjectedPrj.BritishNationalGrid.Replace(
            "PARAMETER[\"Latitude_Of_Origin\"", "PARAMETER[\"Azimuth\",1.0],PARAMETER[\"Latitude_Of_Origin\"", StringComparison.Ordinal))
            .Should().BeNull();

    private static string Fixture(string name) =>
        (string)typeof(EsriProjectedPrj).GetField(name)!.GetValue(null)!;
}
