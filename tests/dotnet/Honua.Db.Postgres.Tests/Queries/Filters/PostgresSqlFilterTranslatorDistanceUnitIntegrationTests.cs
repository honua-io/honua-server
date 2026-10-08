// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Fes20;
using Honua.Db.Postgres.Queries.Filters;
using Honua.TestKit;

namespace Honua.Db.Postgres.Tests.Queries.Filters;

/// <summary>
/// FES distances carry a unit of measure; the parser normalises them to metres. On a projected
/// layer the planar predicate measures in the CRS's native unit, so the translator must convert
/// (honua-server#5462). The oracle is exact unit arithmetic: in EPSG:2264 (US survey feet) the
/// fixture points sit 50 ftUS = 15.24 m and 100 ftUS = 30.48 m from the filter point.
/// </summary>
[Collection("Database")]
public sealed class PostgresSqlFilterTranslatorDistanceUnitIntegrationTests(PostgresFixture fixture)
{
    private const int FootSrid = 2264;

    [Theory]
    [InlineData("DWithin", "m", 20, new long[] { 1 })]
    [InlineData("Beyond", "m", 20, new long[] { 2 })]
    [InlineData("DWithin", "ft", 65, new long[] { 1 })]
    [InlineData("DWithin", "km", 0.04, new long[] { 1, 2 })]
    public async Task FesDistanceOnUsFootLayer_MeasuresTheRequestedUnit(
        string operation, string uom, double distance, long[] expected)
    {
        var filter = Fes20Parser.ParseFilter(
            System.Xml.Linq.XElement.Parse(FormattableString.Invariant($"""
                <fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0" xmlns:gml="http://www.opengis.net/gml/3.2">
                  <fes:{operation}>
                    <fes:ValueReference>geom</fes:ValueReference>
                    <gml:Point srsName="urn:ogc:def:crs:EPSG::2264"><gml:pos>2000000 700000</gml:pos></gml:Point>
                    <fes:Distance uom="{uom}">{distance}</fes:Distance>
                  </fes:{operation}>
                </fes:Filter>
                """)),
            FootSrid);

        var ids = await SelectAsync(filter);

        ids.Should().Equal(expected);
    }

    [Fact]
    public async Task FesDistanceWithoutUom_KeepsNativeUnitSemantics()
    {
        // A missing uom is tolerated as a raw value; on a projected layer that value has always
        // been read in the CRS unit, so 60 here means 60 ftUS.
        var filter = Fes20Parser.ParseFilter(
            System.Xml.Linq.XElement.Parse("""
                <fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0" xmlns:gml="http://www.opengis.net/gml/3.2">
                  <fes:DWithin>
                    <fes:ValueReference>geom</fes:ValueReference>
                    <gml:Point srsName="urn:ogc:def:crs:EPSG::2264"><gml:pos>2000000 700000</gml:pos></gml:Point>
                    <fes:Distance>60</fes:Distance>
                  </fes:DWithin>
                </fes:Filter>
                """),
            FootSrid);

        var ids = await SelectAsync(filter);

        ids.Should().Equal(1);
    }

    private async Task<long[]> SelectAsync(FilterExpression filter)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("FesDistanceUnits");
        try
        {
            await fixture.ExecuteAsync($"""
                CREATE TABLE {schema}.features (objectid bigint PRIMARY KEY, geom geometry(Point, {FootSrid}));
                INSERT INTO {schema}.features VALUES
                    (1, ST_SetSRID(ST_MakePoint(2000050, 700000), {FootSrid})),
                    (2, ST_SetSRID(ST_MakePoint(2000100, 700000), {FootSrid}));
                """);

            var context = FilterTranslationContext.FromColumns(
                [
                    new FilterTranslationContext.ContextField("objectid", MetadataV2FieldType.BigInteger, IsGeometry: false, IsPrimaryKey: true),
                    new FilterTranslationContext.ContextField("geom", MetadataV2FieldType.Geometry, IsGeometry: true, IsPrimaryKey: false)
                ],
                primaryKeyName: "objectid",
                geometryColumnName: "geom",
                wkid: FootSrid,
                geometryType: GeometryType.Point,
                resourceName: "parcels",
                isGeographic: false);
            var fragment = new PostgresSqlFilterTranslator(geometryColumn: "geom").Translate(filter, context);

            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT objectid FROM {schema}.features WHERE {fragment.Sql} ORDER BY objectid";
            for (var i = 0; i < fragment.Parameters.Count; i++)
            {
                command.Parameters.AddWithValue($"p{i}", fragment.Parameters[i]!);
            }

            var ids = new List<long>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetInt64(0));
            }

            return [.. ids];
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }
}
