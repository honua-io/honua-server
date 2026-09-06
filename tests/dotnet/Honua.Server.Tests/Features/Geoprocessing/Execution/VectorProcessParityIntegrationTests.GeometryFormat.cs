// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

public sealed partial class VectorProcessParityIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task GeometryFormat_AllTargets_PreservePolygonHoleCoordinatesAndSrid()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        await using var fixture = BuildFixture();
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        // Independent rectangular-ring fixture: exterior area 80, hole area 12,
        // retained area 68. The expected topology is constructed from explicit WKT.
        const string wkt = "POLYGON ((0 0, 10 0, 10 8, 0 8, 0 0), (2 2, 2 5, 6 5, 6 2, 2 2))";
        var expected = (Polygon)new WKTReader().Read(wkt);
        expected.SRID = 4326;
        var bytes = new WKBWriter(ByteOrder.LittleEndian, true).Write(expected);
        var encoded = Convert.ToBase64String(bytes);
        foreach (var target in new[] { "wkt", "geojson", "wkb", "ewkt" })
        {
            var body = JsonSerializer.Serialize(new { response = "document", inputs = new { geometry = encoded, target } });
            var jobId = await SubmitJobAsync(client, "conversion.geometry-format", body);
            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be("successful");
            using var results = await GetResultsAsync(fixture, client, jobId);
            var scalar = results.RootElement.GetProperty("outputScalar");
            var converted = scalar.GetProperty("value");
            converted.GetProperty("processId").GetString().Should().Be("conversion.geometry-format");
            converted.GetProperty("format").GetString().Should().Be(target);
            converted.GetProperty("srid").GetInt32().Should().Be(4326);
            converted.GetProperty("contentType").GetString().Should().Be(target switch
            {
                "geojson" => "application/geo+json",
                "wkb" => "application/wkb",
                _ => "text/plain"
            });
            var value = converted.GetProperty("value");
            Geometry parsed;
            switch (target)
            {
                case "geojson":
                    value.GetProperty("type").GetString().Should().Be("Polygon");
                    value.GetProperty("coordinates").GetArrayLength().Should().Be(2);
                    parsed = new GeoJsonReader().Read<Geometry>(value.GetRawText());
                    break;
                case "wkb":
                    parsed = new WKBReader { HandleSRID = true }.Read(Convert.FromBase64String(value.GetString()!));
                    parsed.SRID.Should().Be(4326);
                    break;
                case "ewkt":
                    value.GetString().Should().StartWith("SRID=4326;");
                    parsed = new WKTReader().Read(value.GetString()![10..]);
                    break;
                default:
                    value.GetString().Should().StartWith("POLYGON").And.NotContain("SRID=");
                    parsed = new WKTReader().Read(value.GetString()!);
                    break;
            }
            parsed.Should().BeOfType<Polygon>();
            var polygon = (Polygon)parsed;
            polygon.EqualsTopologically(expected).Should().BeTrue();
            polygon.Area.Should().Be(68);
            polygon.NumInteriorRings.Should().Be(1);
            polygon.ExteriorRing.Coordinates.Select(coordinate => (coordinate.X, coordinate.Y))
                .Should().BeEquivalentTo(expected.ExteriorRing.Coordinates.Select(coordinate => (coordinate.X, coordinate.Y)));
            polygon.GetInteriorRingN(0).Coordinates.Select(coordinate => (coordinate.X, coordinate.Y))
                .Should().BeEquivalentTo(expected.GetInteriorRingN(0).Coordinates.Select(coordinate => (coordinate.X, coordinate.Y)));
        }

        using var invalidBody = new StringContent(JsonSerializer.Serialize(new
        {
            response = "document",
            inputs = new { geometry = encoded, target = "invalid-target" }
        }), Encoding.UTF8, "application/json");
        using var invalid = await client.PostAsync("/ogc/processes/processes/conversion.geometry-format/execution", invalidBody);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await invalid.Content.ReadAsStringAsync()).Should().Contain("target");
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);
    }
}
