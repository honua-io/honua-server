// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Crs;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Infrastructure.Crs;

/// <summary>
/// Unit tests for <see cref="CrsLinearUnitFactor"/>.
/// </summary>
public sealed class CrsLinearUnitFactorTests
{
    [UnitTest]
    public void Resolve_HarnFootWktOutsideTheSridTable_ReturnsUsSurveyFoot()
    {
        // EPSG:2866 is NAD83(HARN) / Arizona East (ftUS). The static SRID switch
        // covers 2867-2885 and leaves 2866 as metres.
        const string wkt =
            """PROJCS["NAD83(HARN) / Arizona East (ftUS)",GEOGCS["NAD83(HARN)",DATUM["NAD83_High_Accuracy_Reference_Network",SPHEROID["GRS 1980",6378137,298.257222101]],PRIMEM["Greenwich",0],UNIT["degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],PARAMETER["latitude_of_origin",31],UNIT["US survey foot",0.3048006096012192],AUTHORITY["EPSG","2866"]]""";

        var factor = CrsLinearUnitFactor.Resolve(proj4Text: null, wkt, isGeographic: false);

        factor.Should().BeApproximately(CrsLinearUnitFactor.UsSurveyFootMeters, 1e-15);
    }

    [UnitTest]
    public void Resolve_ToMeter_WinsOverUnitsAndWkt()
    {
        const string proj4 = "+proj=tmerc +to_meter=0.3048006096012192 +units=m";
        const string wkt = """PROJCS["x",UNIT["metre",1]]""";

        var factor = CrsLinearUnitFactor.Resolve(proj4, wkt, isGeographic: false);

        factor.Should().BeApproximately(CrsLinearUnitFactor.UsSurveyFootMeters, 1e-15);
    }

    [UnitTest]
    public void Resolve_UsFtUnits_ReturnsSurveyFoot()
    {
        var factor = CrsLinearUnitFactor.Resolve("+proj=tmerc +units=us-ft", wkt: null, isGeographic: false);

        factor.Should().Be(CrsLinearUnitFactor.UsSurveyFootMeters);
    }

    [UnitTest]
    public void Resolve_GeographicWkt_ReturnsDegreeFactor()
    {
        const string wkt =
            """GEOGCS["WGS 84",DATUM["WGS_1984",SPHEROID["WGS 84",6378137,298.257223563]],PRIMEM["Greenwich",0],UNIT["degree",0.0174532925199433],AUTHORITY["EPSG","4326"]]""";

        var factor = CrsLinearUnitFactor.Resolve(proj4Text: null, wkt, isGeographic: true);

        factor.Should().BeApproximately(Math.PI / 180d, 1e-12);
    }

    [UnitTest]
    public void Resolve_Wkt2Projected_UsesLengthUnitNotTheDatumEllipsoid()
    {
        const string wkt =
            """PROJCRS["x",BASEGEOGCRS["WGS 84",ELLIPSOID["WGS 84",6378137,298.257223563],ANGLEUNIT["degree",0.0174532925199433]],CS[Cartesian,2],LENGTHUNIT["US survey foot",0.3048006096012192]]""";

        var factor = CrsLinearUnitFactor.Resolve(proj4Text: null, wkt, isGeographic: false);

        factor.Should().BeApproximately(CrsLinearUnitFactor.UsSurveyFootMeters, 1e-15);
    }
}
