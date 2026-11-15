// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Postgres.Tests.Features.Import;

// ESRI-dialect .prj text as desktop GIS and GDAL's ESRI Shapefile driver write it: ESRI names and no AUTHORITY node.
internal static class EsriProjectedPrj
{
    private const string Metre = "UNIT[\"Meter\",1.0]";
    private const string FootUs = "UNIT[\"Foot_US\",0.3048006096012192]";
    private const string Grs80 = "SPHEROID[\"GRS_1980\",6378137.0,298.257222101]";
    private const string LongIsland = "984250.0,0.0,-74.0,40.66666666666666,41.03333333333333,40.16666666666666";

    private static readonly string Nad83 = Geographic("GCS_North_American_1983", "D_North_American_1983", Grs80);
    private static readonly string Gda94 = Geographic("GCS_GDA_1994", "D_GDA_1994", Grs80);

    public static readonly string NewYorkLongIslandFtUs =
        new Crs("NAD_1983_StatePlane_New_York_Long_Island_FIPS_3104_Feet", Nad83, FootUs).Lcc(LongIsland);

    public static readonly string NewYorkLongIsland2011FtUs = new Crs(
        "NAD_1983_2011_StatePlane_New_York_Long_Isl_FIPS_3104_Ft_US",
        Geographic("GCS_NAD_1983_2011", "D_NAD_1983_2011", Grs80), FootUs).Lcc(LongIsland);

    public static readonly string NewYorkLongIslandInternationalFeet = new Crs(
        "NAD_1983_StatePlane_New_York_Long_Island_FIPS_3104_Intl_Feet", Nad83, "UNIT[\"Foot\",0.3048]").Lcc(LongIsland);

    public static readonly string CaliforniaZone5FtUs = new Crs("NAD_1983_StatePlane_California_V_FIPS_0405_Feet", Nad83, FootUs)
        .Lcc("6561666.666666666,1640416.666666667,-118.0,34.03333333333333,35.46666666666667,33.5");

    public static readonly string Lambert93 = new Crs(
        "RGF_1993_Lambert_93", Geographic("GCS_RGF_1993", "D_RGF_1993", Grs80), Metre).Lcc("700000.0,6600000.0,3.0,49.0,44.0,46.5");

    public static readonly string BritishNationalGrid = new Crs("British_National_Grid",
        Geographic("GCS_OSGB_1936", "D_OSGB_1936", "SPHEROID[\"Airy_1830\",6377563.396,299.3249646]"), Metre)
        .TransverseMercator("400000.0,-100000.0,-2.0,0.9996012717,49.0");

    public static readonly string BcAlbers =
        new Crs("NAD_1983_BC_Environment_Albers", Nad83, Metre).Albers("1000000.0,0.0,-126.0,50.0,58.5,45.0");

    public static readonly string MgaZone55 =
        new Crs("GDA_1994_MGA_Zone_55", Gda94, Metre).TransverseMercator("500000.0,10000000.0,147.0,0.9996,0.0");

    public static readonly string AustralianAlbers =
        new Crs("GDA_1994_Australia_Albers", Gda94, Metre).Albers("0.0,0.0,132.0,-18.0,-36.0,0.0");

    private static string Geographic(string name, string datum, string spheroid) =>
        $"GEOGCS[\"{name}\",DATUM[\"{datum}\",{spheroid}],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]";

    private sealed record Crs(string Name, string GeographicCrs, string Unit)
    {
        private static readonly string[] _conic =
            ["False_Easting", "False_Northing", "Central_Meridian", "Standard_Parallel_1", "Standard_Parallel_2", "Latitude_Of_Origin"];

        private static readonly string[] _transverseMercator =
            ["False_Easting", "False_Northing", "Central_Meridian", "Scale_Factor", "Latitude_Of_Origin"];

        public string Lcc(string values) => Projected("Lambert_Conformal_Conic", Parameters(_conic, values));

        public string Albers(string values) => Projected("Albers", Parameters(_conic, values));

        public string TransverseMercator(string values) => Projected("Transverse_Mercator", Parameters(_transverseMercator, values));

        private string Projected(string projection, string parameters) =>
            $"PROJCS[\"{Name}\",{GeographicCrs},PROJECTION[\"{projection}\"],{parameters},{Unit}]";

        private static string Parameters(string[] names, string values) =>
            string.Join(',', names.Zip(values.Split(',')).Select(pair => $"PARAMETER[\"{pair.First}\",{pair.Second}]"));
    }
}
