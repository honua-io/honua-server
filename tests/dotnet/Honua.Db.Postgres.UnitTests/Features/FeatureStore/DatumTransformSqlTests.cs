// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Shared.Models;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Infrastructure.Crs;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed class DatumTransformSqlTests
{
    [UnitTheory]
    [InlineData("f.geometry", 4326, 3857)]
    [InlineData("f.geometry::geometry", 4269, 3857)]
    [InlineData("f.geometry", 3857, 4326)]
    public void CachedTransformExpression_MatchesTheBuiltExpression(string operand, int fromSrid, int toSrid)
    {
        var built = DatumTransformSql.BuildTransformExpression(operand, toSrid, selection: null, fromSrid);

        DatumTransformSql.CachedTransformExpression(operand, fromSrid, toSrid).Should().Be(built);
        DatumTransformSql.CachedTransformExpression(operand, fromSrid, toSrid)
            .Should().BeSameAs(DatumTransformSql.CachedTransformExpression(operand, fromSrid, toSrid));
    }

    [UnitTheory]
    [InlineData("f.geometry", 3857)]
    [InlineData("f.geometry", 4326)]
    public void CachedTransformExpressionForRuntimeSrid_MatchesTheBuiltExpression(string operand, int toSrid)
        => DatumTransformSql.CachedTransformExpressionForRuntimeSrid(operand, toSrid)
            .Should().Be(DatumTransformSql.BuildTransformExpressionForRuntimeSrid(operand, toSrid));

    [UnitTest]
    public void BuildTransformExpression_NoSelection_EmitsTwoArgumentForm()
    {
        var sql = DatumTransformSql.BuildTransformExpression("geom", 4326, selection: null);

        sql.Should().Be("ST_Transform(geom, 4326)");
    }

    [UnitTest]
    public void BuildTransformExpression_SelectionWithoutPipeline_EmitsTwoArgumentForm()
    {
        var selection = new DatumTransformationSelection
        {
            Name = "identity",
            FromSrid = 4269,
            ToSrid = 4326,
            ProjPipeline = null
        };

        var sql = DatumTransformSql.BuildTransformExpression("geom", 4326, selection);

        sql.Should().Be("ST_Transform(geom, 4326)");
    }

    [UnitTest]
    public void BuildTransformExpression_SelectionWithPipeline_EmitsOperationPipelineForm()
    {
        var selection = new DatumTransformationSelection
        {
            Name = "NAD_1983_To_WGS_1984_1",
            FromSrid = 4269,
            ToSrid = 4326,
            ProjPipeline = "+proj=pipeline +step +proj=noop"
        };

        var sql = DatumTransformSql.BuildTransformExpression("geom", 4326, selection);

        sql.Should().Be("ST_TransformPipeline(geom, '+proj=pipeline +step +proj=noop', 4326)");
    }

    [UnitTest]
    public void BuildTransformExpression_ReverseSelection_InvertsPipeline()
    {
        // A reverse selection (e.g. NAD83 -> NAD27) carries the forward NADCON pipeline verbatim
        // with TransformForward=false. Emitting it as-is would shift coordinates the wrong way —
        // roughly twice the intended NADCON correction. The emitted pipeline must invert
        // the direction-sensitive hgridshift step (+inv) and reverse the step order.
        var selection = new DatumTransformationSelection
        {
            Name = "NAD_1927_To_NAD_1983_NADCON",
            FromSrid = 4326,
            ToSrid = 4269,
            ProjPipeline = "+proj=pipeline +step +proj=unitconvert +xy_in=deg +xy_out=rad +step +proj=hgridshift +grids=us_noaa_conus.tif +step +proj=unitconvert +xy_in=rad +xy_out=deg",
            TransformForward = false
        };

        var sql = DatumTransformSql.BuildTransformExpression("geom", 4269, selection);

        sql.Should().Be(
            "ST_TransformPipeline(geom, '+proj=pipeline " +
            "+step +proj=unitconvert +xy_in=rad +xy_out=deg +inv " +
            "+step +proj=hgridshift +grids=us_noaa_conus.tif +inv " +
            "+step +proj=unitconvert +xy_in=deg +xy_out=rad +inv', 4269)");
    }

    [UnitTest]
    public void BuildTransformExpression_ForwardSelection_KeepsPipelineVerbatim()
    {
        // The forward direction must be emitted exactly as catalogued (no +inv).
        var selection = new DatumTransformationSelection
        {
            Name = "NAD_1927_To_NAD_1983_NADCON",
            FromSrid = 4267,
            ToSrid = 4269,
            ProjPipeline = "+proj=pipeline +step +proj=hgridshift +grids=us_noaa_conus.tif",
            TransformForward = true
        };

        var sql = DatumTransformSql.BuildTransformExpression("geom", 4269, selection);

        sql.Should().Be("ST_TransformPipeline(geom, '+proj=pipeline +step +proj=hgridshift +grids=us_noaa_conus.tif', 4269)");
    }

    [UnitTest]
    public void BuildTransformExpression_ReverseNoopSelection_StaysNoop()
    {
        // +proj=noop is self-inverse; a reverse identity selection must stay identity.
        var selection = new DatumTransformationSelection
        {
            Name = "NAD_1983_To_WGS_1984_1",
            FromSrid = 4326,
            ToSrid = 4269,
            ProjPipeline = "+proj=noop",
            TransformForward = false
        };

        var sql = DatumTransformSql.BuildTransformExpression("geom", 4269, selection);

        sql.Should().Be("ST_SetSRID(geom, 4269)");
    }

    [UnitTest]
    public void BuildTransformExpression_PipelineWithSingleQuote_IsEscaped()
    {
        var selection = new DatumTransformationSelection
        {
            Name = "quoted",
            FromSrid = 4269,
            ToSrid = 4326,
            ProjPipeline = "a'b"
        };

        var sql = DatumTransformSql.BuildTransformExpression("geom", 4326, selection);

        sql.Should().Be("ST_TransformPipeline(geom, 'a''b', 4326)");
    }

    [UnitTheory]
    [InlineData(3857)]
    [InlineData(102100)]
    [InlineData(900913)]
    [InlineData(3785)]
    public void BuildTransformExpression_GeographicSourceToWebMercator_ClampsBeforeTransform(int toSrid)
    {
        var sql = DatumTransformSql.BuildTransformExpression("geom", toSrid, selection: null, fromSrid: 4326);

        AssertPointClamp(sql, "geom", 4326, toSrid);
    }

    [UnitTest]
    public void BuildTransformExpression_OtherGeographicSourceToWebMercator_UsesSourceSridOnClip()
    {
        var sql = DatumTransformSql.BuildTransformExpression("geom", 102100, selection: null, fromSrid: 4269);

        AssertPointClamp(sql, "geom", 4269, 102100);
    }

    [UnitTest]
    public void BuildTransformExpression_GeographicSourceToNonMercator_DoesNotClamp()
    {
        var sql = DatumTransformSql.BuildTransformExpression("geom", 4269, selection: null, fromSrid: 4326);

        sql.Should().Be("ST_Transform(geom, 4269)");
        sql.Should().NotContain("ST_ClipByBox2D");
        sql.Should().NotContain("85.0511287798066");
    }

    [UnitTest]
    public void BuildTransformExpression_ProjectedSourceToWebMercator_DoesNotClamp()
    {
        var sql = DatumTransformSql.BuildTransformExpression("geom", 3857, selection: null, fromSrid: 26911);

        sql.Should().Be("ST_Transform(geom, 3857)");
        sql.Should().NotContain("ST_ClipByBox2D");
    }

    [UnitTest]
    public void BuildTransformExpression_UnknownSourceToWebMercator_DoesNotClamp()
    {
        var sql = DatumTransformSql.BuildTransformExpression("geom", 3857, selection: null);

        sql.Should().Be("ST_Transform(geom, 3857)");
        sql.Should().NotContain("ST_ClipByBox2D");
    }

    [UnitTest]
    public void BuildTransformExpression_GeographicToWebMercator_PipelineClampsBeforeTransform()
    {
        var selection = new DatumTransformationSelection
        {
            Name = "WGS_1984_To_WebMercator",
            FromSrid = 4326,
            ToSrid = 3857,
            ProjPipeline = "+proj=pipeline +step +proj=merc"
        };

        var sql = DatumTransformSql.BuildTransformExpression("geom", 3857, selection, fromSrid: 4326);

        sql.Should().StartWith("ST_TransformPipeline(CASE ST_GeometryType(geom)");
        sql.Should().Contain("LEAST(GREATEST(ST_Y(geom), -85.0511287798066), 85.0511287798066)");
        sql.Should().Contain("'+proj=pipeline +step +proj=merc', 3857)");
    }

    [UnitTest]
    public void BuildTransformExpression_NoopToWebMercator_DoesNotClamp()
    {
        var selection = new DatumTransformationSelection
        {
            Name = "identity",
            FromSrid = 4326,
            ToSrid = 3857,
            ProjPipeline = "+proj=noop"
        };

        var sql = DatumTransformSql.BuildTransformExpression("geom", 3857, selection, fromSrid: 4326);

        sql.Should().Be("ST_SetSRID(geom, 3857)");
        sql.Should().NotContain("ST_ClipByBox2D");
    }

    [UnitTest]
    public void BuildTransformExpression_ProjectedEndpoints_ComposeExplicitDatumPipeline()
    {
        var selection = new DatumTransformationSelection
        {
            Name = "NAD_1927_To_NAD_1983_NADCON",
            FromSrid = 4267,
            ToSrid = 4269,
            Wkid = 1241,
            ProjPipeline = "+proj=pipeline +step +proj=hgridshift +grids=us_noaa_conus.tif",
            TransformForward = true
        };

        var sql = DatumTransformSql.BuildTransformExpression("geom", 26917, selection, fromSrid: 26717);

        sql.Should().Be(
            "ST_Transform(ST_TransformPipeline(ST_Transform(geom, 4267), '+proj=pipeline +step +proj=hgridshift +grids=us_noaa_conus.tif', 4269), 26917)");
        sql.Should().NotBe("ST_Transform(geom, 26917)");
    }

    [UnitTest]
    public void PostgresCrsUnitSql_MetersPerProjectedUnit_UsesAnchoredMeterWordBoundaryRegex()
    {
        var sql = PostgresCrsUnitSql.MetersPerProjectedUnit(3857);

        sql.Should().Contain(@"~ '\+units=m(\s|$)'");
        sql.Should().NotContain("ILIKE '%+units=m%'");
    }

    [UnitTheory]
    [InlineData(
        """PROJCRS["ETRS89 / UTM zone 32N",BASEGEOGCRS["ETRS89",DATUM["European Terrestrial Reference System 1989",ELLIPSOID["GRS 1980",6378137,298.257222101]],CS[ellipsoidal,2],AXIS["latitude",north],AXIS["longitude",east]],CONVERSION["UTM zone 32N",METHOD["Transverse Mercator"]],CS[Cartesian,2],AXIS["(E)",east],AXIS["(N)",north],LENGTHUNIT["metre",1]]""",
        AxisOrder.EastNorth)]
    [InlineData(
        """PROJECTEDCRS["WGS 84 / UTM zone 10N",BASEGEODCRS["WGS 84",DATUM["World Geodetic System 1984",ELLIPSOID["WGS 84",6378137,298.257223563]],CS[ellipsoidal,2],AXIS["lat",north],AXIS["lon",east]],CONVERSION["UTM zone 10N",METHOD["Transverse Mercator"]],CS[Cartesian,2],AXIS["Easting",east],AXIS["Northing",north],LENGTHUNIT["metre",1]]""",
        AxisOrder.EastNorth)]
    [InlineData(
        """PROJCS["Custom Mercator",GEOGCS["WGS 84",DATUM["WGS_1984",SPHEROID["WGS 84",6378137,298.257223563]],PRIMEM["Greenwich",0],UNIT["degree",0.0174532925199433],AXIS["Lat",NORTH],AXIS["Lon",EAST]],PROJECTION["Mercator_1SP"],PARAMETER["semi_major_axis",6378137],UNIT["metre",1],AXIS["X",EAST],AXIS["Y",NORTH]]""",
        AxisOrder.EastNorth)]
    [InlineData(
        """PROJCRS["ETRS89-extended / LAEA Europe",BASEGEOGCRS["ETRS89",DATUM["European Terrestrial Reference System 1989",ELLIPSOID["GRS 1980",6378137,298.257222101]],CS[ellipsoidal,2],AXIS["latitude",north],AXIS["longitude",east]],CONVERSION["Europe Equal Area",METHOD["Lambert Azimuthal Equal Area"]],CS[Cartesian,2],AXIS["Northing (Y)",north],AXIS["Easting (X)",east],LENGTHUNIT["metre",1]]""",
        AxisOrder.NorthEast)]
    public void PostgresCrsRegistry_TryParseAxisOrder_IgnoresInnerBaseGeographicAxesAndSemiMajorAxisParameter(
        string wkt, AxisOrder expected)
    {
        PostgresCrsRegistry.TryParseAxisOrder(wkt, out var axisOrder).Should().BeTrue();
        axisOrder.Should().Be(expected);
    }

    [UnitTheory]
    [InlineData("urn:ogc:def:crs:EPSG:6.9:25831", "http://www.opengis.net/def/crs/EPSG/0/25831")]
    [InlineData("urn:x-ogc:def:crs:EPSG::4326", "http://www.opengis.net/def/crs/EPSG/0/4326")]
    [InlineData("urn:ogc:def:crs:OGC:1.3:CRS84", "http://www.opengis.net/def/crs/OGC/1.3/CRS84")]
    [InlineData("urn:ogc:def:crs:OGC:2:84", "http://www.opengis.net/def/crs/OGC/1.3/CRS84")]
    [InlineData("CRS:84", "http://www.opengis.net/def/crs/OGC/1.3/CRS84")]
    public void PostgresCrsRegistry_NormalizeIdentifier_SupportsVersionedUrnsAndCrs84Aliases(
        string input, string expected)
    {
        PostgresCrsRegistry.NormalizeIdentifier(input).Should().Be(expected);
    }

    [UnitTest]
    public async Task PostgresCrsRegistry_WithSharedCache_PersistsAcrossScopedInstances()
    {
        var sharedCache = new PostgresCrsRegistryCache();
        var provider = new ThrowingConnectionProvider();
        var scope1Registry = new PostgresCrsRegistry(provider, NullLogger<PostgresCrsRegistry>.Instance, sharedCache);
        var scope2Registry = new PostgresCrsRegistry(provider, NullLogger<PostgresCrsRegistry>.Instance, sharedCache);

        var warmed = await scope1Registry.ResolveAsync("urn:ogc:def:crs:OGC:2:84");
        warmed.Should().NotBeNull();
        warmed!.Value.AxisOrder.Should().Be(AxisOrder.EastNorth);

        var entry = new PostgresCrsRegistry.CrsCacheEntry(
            new CrsDefinition("http://www.opengis.net/def/crs/EPSG/0/25831", 25831, AxisOrder.EastNorth, false),
            DateTimeOffset.UtcNow);
        sharedCache.SridCache.TryAdd(25831, entry);

        // Second scoped registry resolves SRID 25831 via versioned URN from the shared cache without touching the DB.
        var fromSecondScope = await scope2Registry.ResolveAsync("urn:ogc:def:crs:EPSG:6.9:25831");
        fromSecondScope.Should().NotBeNull();
        fromSecondScope!.Value.Srid.Should().Be(25831);

        // A directly constructed registry without the shared cache uses its own isolated cache.
        var isolatedRegistry = new PostgresCrsRegistry(provider, NullLogger<PostgresCrsRegistry>.Instance);
        isolatedRegistry.Invoking(r => r.ResolveBySridAsync(25831).AsTask())
            .Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class ThrowingConnectionProvider : IAdoNetDatabaseConnectionProvider
    {
        public string ProviderName => "Test";
        public bool IsHealthy => false;
        public string GetConnectionString() => string.Empty;
        public Task<System.Data.Common.DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No database connection in unit test.");
        public Task<(System.Data.Common.DbConnection Connection, System.Data.Common.DbTransaction Transaction)> OpenTransactionAsync(
            System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No database connection in unit test.");
        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => operation();
        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => operation();
        public Task<NpgsqlConnectionLease> OpenNpgsqlConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No database connection in unit test.");
        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void AssertPointClamp(string sql, string operand, int fromSrid, int toSrid)
    {
        sql.Should().StartWith("ST_Transform(CASE ST_GeometryType(" + operand + ")");
        sql.Should().Contain("WHEN 'ST_Point' THEN CASE ST_Zmflag(" + operand + ")");
        sql.Should().Contain("LEAST(GREATEST(ST_Y(" + operand + "), -85.0511287798066), 85.0511287798066)");
        sql.Should().Contain("WHEN 'ST_MultiPoint' THEN (SELECT ST_Multi(ST_Collect(");
        sql.Should().Contain("FROM ST_Dump(" + operand + ") AS pt)");
        sql.Should().Contain(
            "ELSE ST_ClipByBox2D(" + operand + ", ST_MakeEnvelope(-180, -85.0511287798066, 180, 85.0511287798066, " + fromSrid + ")) END, " + toSrid + ")");
    }
}
