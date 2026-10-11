// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Native ImageServer catalog SOAP filters are checked against real PostGIS rows,
/// independently of the protocol's filter evaluator and REST query response.
/// </summary>
[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerNativeCatalogTests : IAsyncLifetime
{
    private const string ArcGisNamespace = "http://www.esri.com/schemas/ArcGIS/10.8";
    private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("absent")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("4326")]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.ImageServer, "GetCatalogItemCount")]
    [InterfaceOperation(TestProtocols.ImageServer, "GetCatalogItemIDs")]
    [InterfaceOperation(TestProtocols.ImageServer, "GetCatalogItems")]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task CatalogSoap_DefaultOrNilOutputSpatialReference_MatchesIndependentSql(string spatialReference)
    {
        await RasterIntegrationTestData.RunWithIssue522MosaicAsync(_fixture, async () =>
        {
            var before = await ReadCatalogAsync();
            before.Should().HaveCount(3);
            await AssertCatalogOperationsAsync("1=1", spatialReference, before);
            (await ReadCatalogAsync()).Should().Equal(before);
        });
    }

    [IntegrationTheory]
    [InlineData("in")]
    [InlineData("not-in")]
    [InlineData("mixed")]
    [InlineData("negative")]
    [InlineData("not-in-negative")]
    [InlineData("in-null")]
    [InlineData("not-in-null")]
    [InlineData("not-membership-null")]
    [InlineData("not-null-left")]
    [InlineData("and-null")]
    [InlineData("or-null")]
    [InlineData("not-and-null")]
    [InlineData("not-or-null")]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.ImageServer, "GetCatalogItemCount")]
    [InterfaceOperation(TestProtocols.ImageServer, "GetCatalogItemIDs")]
    [InterfaceOperation(TestProtocols.ImageServer, "GetCatalogItems")]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task CatalogSoap_MembershipAndNegativeFilters_MatchIndependentSql(string mode)
    {
        await RasterIntegrationTestData.RunWithIssue522MosaicAsync(_fixture, async () =>
        {
            var before = await ReadCatalogAsync();
            var selectedId = before.Single(row => row.Name == "east").Id;
            var missingId = checked(before.Max(row => row.Id) + 1000);
            var id = mode.Contains("negative", StringComparison.Ordinal) ? missingId : selectedId;
            var clause = mode switch
            {
                "not-in" or "not-in-negative" => FormattableString.Invariant($"(OBJECTID NOT IN ({id}))"),
                "not-membership-null" => FormattableString.Invariant($"NOT (OBJECTID IN ({id}, NULL))"),
                "not-null-left" => "NOT (NULL IN (NULL))",
                "and-null" => FormattableString.Invariant($"OBJECTID IN ({id}, NULL) AND OBJECTID = {id}"),
                "or-null" => FormattableString.Invariant($"OBJECTID IN ({id}, NULL) OR Name = 'west'"),
                "not-and-null" => FormattableString.Invariant($"NOT (OBJECTID IN ({id}, NULL) AND OBJECTID = {id})"),
                "not-or-null" => FormattableString.Invariant($"NOT (OBJECTID IN ({id}, NULL) OR Name = 'west')"),
                "in-null" => FormattableString.Invariant($"(OBJECTID IN ({id}, NULL))"),
                "not-in-null" => FormattableString.Invariant($"(OBJECTID NOT IN ({id}, NULL))"),
                "mixed" => FormattableString.Invariant($"(OBJECTID IN ({id}) AND Name LIKE 'e%') OR Name = 'west'"),
                _ => FormattableString.Invariant($"(OBJECTID IN ({id}))")
            };
            var expected = await ReadFilteredCatalogAsync(mode, id);
            if (mode == "negative")
            {
                expected.Should().BeEmpty();
            }

            await AssertCatalogOperationsAsync(clause, "4326", expected);
            (await ReadCatalogAsync()).Should().Equal(before);
        });
    }

    [IntegrationTheory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("invalid")]
    [InlineData("empty")]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task CatalogSoap_NonNilInvalidSpatialReference_RemainsBadRequest(string spatialReference)
    {
        await RasterIntegrationTestData.RunWithIssue522MosaicAsync(_fixture, async () =>
        {
            var before = await ReadCatalogAsync();
            foreach (var operation in OperationsToCheck)
            {
                var response = await PostSoapAsync(operation, "1=1", spatialReference);
                response.Status.Should().Be(HttpStatusCode.BadRequest, response.Body);
            }

            (await ReadCatalogAsync()).Should().Equal(before);
        });
    }

    private static readonly string[] OperationsToCheck = ["GetCatalogItemCount", "GetCatalogItemIDs", "GetCatalogItems"];

    private async Task AssertCatalogOperationsAsync(string where, string spatialReference, IReadOnlyList<SqlRaster> expected)
    {
        foreach (var operation in OperationsToCheck)
        {
            var response = await PostSoapAsync(operation, where, spatialReference);
            response.Status.Should().Be(HttpStatusCode.OK, response.Body);
            var document = XDocument.Parse(response.Body);
            var result = document.Descendants().Single(element => element.Name.LocalName == "Result");
            if (operation == "GetCatalogItemCount")
            {
                int.Parse(result.Value, CultureInfo.InvariantCulture).Should().Be(expected.Count);
            }
            else if (operation == "GetCatalogItemIDs")
            {
                result.Descendants().Where(element => element.Name.LocalName == "Int")
                    .Select(element => long.Parse(element.Value, CultureInfo.InvariantCulture))
                    .Should().BeEquivalentTo(expected.Select(row => row.Id));
            }
            else
            {
                var records = result.Descendants().Where(element => element.Name.LocalName == "Record").ToArray();
                records.Should().HaveCount(expected.Count);
                var ids = records.Select(record => long.Parse(record.Descendants()
                    .First(element => element.Name.LocalName == "Value").Value, CultureInfo.InvariantCulture)).ToArray();
                ids.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(expected.Select(row => row.Id));
                foreach (var record in records)
                {
                    var cells = record.Descendants().Where(element => element.Name.LocalName == "Value").ToArray();
                    cells.Should().HaveCount(3);
                    var row = expected.Single(value => value.Id == long.Parse(cells[0].Value, CultureInfo.InvariantCulture));
                    cells[1].Value.Should().Be(row.Name);
                    var points = cells[2].Descendants().Where(element => element.Name.LocalName == "Point")
                        .Select(point => (
                            X: double.Parse(point.Elements().Single(element => element.Name.LocalName == "X").Value, CultureInfo.InvariantCulture),
                            Y: double.Parse(point.Elements().Single(element => element.Name.LocalName == "Y").Value, CultureInfo.InvariantCulture)))
                        .ToArray();
                    points.Should().HaveCount(5);
                    points[0].Should().Be(points[^1]);
                    points.Min(point => point.X).Should().Be(row.XMin);
                    points.Max(point => point.X).Should().Be(row.XMax);
                    points.Min(point => point.Y).Should().Be(row.YMin);
                    points.Max(point => point.Y).Should().Be(row.YMax);
                }
            }
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> PostSoapAsync(string operation, string where, string spatialReference)
    {
        XNamespace arc = ArcGisNamespace;
        XNamespace xsi = XsiNamespace;
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        var filter = new XElement(arc + "QueryFilter",
            new XElement(arc + "WhereClause", where), new XElement(arc + "SubFields", "OBJECTID,Name,Shape"));
        if (spatialReference != "absent")
        {
            filter.Add(spatialReference switch
            {
                "true" or "1" or "false" or "0" => new XElement(arc + "OutputSpatialReference", new XAttribute(xsi + "nil", spatialReference)),
                "4326" => new XElement(arc + "OutputSpatialReference", new XElement(arc + "WKID", "4326")),
                "invalid" => new XElement(arc + "OutputSpatialReference", new XElement(arc + "WKID", "not-a-wkid")),
                _ => new XElement(arc + "OutputSpatialReference")
            });
        }

        var operationElement = new XElement(arc + operation, new XElement(arc + "Name", "Catalog"), filter);
        if (operation == "GetCatalogItems")
        {
            operationElement.Add(new XElement(arc + "Offset", -1), new XElement(arc + "Limit", -1));
        }

        var envelope = new XElement(soap + "Envelope", new XAttribute(XNamespace.Xmlns + "soap", soap),
            new XAttribute(XNamespace.Xmlns + "xsi", xsi), new XElement(soap + "Body", operationElement));
        using var content = new StringContent(envelope.ToString(), Encoding.UTF8, "text/xml");
        using var response = await _fixture.Client.PostAsync($"/services/{WebAppFixture.TestServiceId}/ImageServer", content);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private Task<List<SqlRaster>> ReadCatalogAsync() => ReadFilteredCatalogAsync("all", 0);

    private async Task<List<SqlRaster>> ReadFilteredCatalogAsync(string mode, long id)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        // Predicates are fixed test inputs and ids are parameters, never SOAP SQL interpolation.
        command.CommandText = mode switch
        {
            "in" or "negative" => SqlSelect + " AND id = @id ORDER BY id",
            "not-in" or "not-in-negative" => SqlSelect + " AND id <> @id ORDER BY id",
            "not-membership-null" => SqlSelect + " AND NOT (id IN (@id, NULL)) ORDER BY id",
            "not-null-left" => SqlSelect + " AND NOT (NULL IN (NULL)) ORDER BY id",
            "and-null" => SqlSelect + " AND (id IN (@id, NULL) AND id = @id) ORDER BY id",
            "or-null" => SqlSelect + " AND (id IN (@id, NULL) OR name = 'west') ORDER BY id",
            "not-and-null" => SqlSelect + " AND NOT (id IN (@id, NULL) AND id = @id) ORDER BY id",
            "not-or-null" => SqlSelect + " AND NOT (id IN (@id, NULL) OR name = 'west') ORDER BY id",
            "in-null" => SqlSelect + " AND id IN (@id, NULL) ORDER BY id",
            "not-in-null" => SqlSelect + " AND id NOT IN (@id, NULL) ORDER BY id",
            "mixed" => SqlSelect + " AND ((id = @id AND name LIKE 'e%') OR name = 'west') ORDER BY id",
            _ => SqlSelect + " ORDER BY id"
        };
        command.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<SqlRaster>();
        while (await reader.ReadAsync())
        {
            rows.Add(new SqlRaster(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetDouble(6), reader.GetDouble(7), reader.GetDouble(8), reader.GetDouble(9)));
        }

        return rows;
    }

    private const string SqlSelect = """
        SELECT id, name, ST_Width(raster), ST_Height(raster), ST_NumBands(raster), ST_SRID(raster),
               ST_XMin(ST_Envelope(raster)), ST_YMin(ST_Envelope(raster)),
               ST_XMax(ST_Envelope(raster)), ST_YMax(ST_Envelope(raster))
        FROM honua.raster_data WHERE layer_id = @layerId
        """;

    private sealed record SqlRaster(long Id, string Name, int Width, int Height, int Bands, int Srid,
        double XMin, double YMin, double XMax, double YMax);
}
