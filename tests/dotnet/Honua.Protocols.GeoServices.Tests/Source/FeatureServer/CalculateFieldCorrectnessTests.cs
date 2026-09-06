// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Npgsql;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class CalculateFieldCorrectnessTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro).ConfigureServices(_ => { });

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.EnableV2ServiceEditingCapabilities(WebAppFixture.TestServiceId, ["Create", "Update", "Delete"]);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Calculate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    public async Task Calculate_FilteredArithmetic_ReadbackPreservesTypesAndInvalidExpressionIsAtomic()
    {
        await using var connection = new NpgsqlConnection(_fixture.Postgres.ConnectionString);
        await connection.OpenAsync();
        using var identifierBuilder = new NpgsqlCommandBuilder();
        var schema = identifierBuilder.QuoteIdentifier(_fixture.CurrentSchema!);
        await using (var seed = new NpgsqlCommand($$"""
            INSERT INTO {{schema}}.features(objectid, layer_id, attributes) VALUES
            (73901, 0, '{"name":"alpha","category":"selected","population":7,"area_sq_km":1.5}'),
            (73902, 0, '{"name":"beta","category":"selected","population":11,"area_sq_km":2.25}'),
            (73903, 0, '{"name":"excluded","category":"other","population":19,"area_sq_km":4.5}');
            """, connection))
        {
            await seed.ExecuteNonQueryAsync();
        }

        var route = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/0";
        async Task<JsonDocument> CalculateAsync(string expression)
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["where"] = "objectid >= 73901 AND category = 'selected'",
                ["calcExpression"] = expression,
                ["f"] = "json"
            });
            using var response = await _fixture.Client.PostAsync(route + "/calculate", content);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }

        async Task AssertRowsAsync()
        {
            using var response = await _fixture.Client.GetAsync(route +
                "/query?where=objectid%20%3E%3D%2073901&outFields=objectid,name,population,area_sq_km&orderByFields=objectid&returnGeometry=false&f=json");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var rows = document.RootElement.GetProperty("features").EnumerateArray()
                .Select(feature => feature.GetProperty("attributes")).ToArray();
            rows.Should().HaveCount(3);
            // Independent arithmetic oracle: (7*3)+2=23, (11*3)+2=35;
            // 1.5/2+0.125=0.875, 2.25/2+0.125=1.25. The third row is excluded.
            var expected = new[] { (73901L, "alpha", 23L, 0.875), (73902L, "beta", 35L, 1.25), (73903L, "excluded", 19L, 4.5) };
            for (var index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                row.GetProperty("objectid").GetInt64().Should().Be(expected[index].Item1);
                row.GetProperty("name").GetString().Should().Be(expected[index].Item2);
                row.GetProperty("population").ValueKind.Should().Be(JsonValueKind.Number);
                row.GetProperty("population").GetInt64().Should().Be(expected[index].Item3);
                row.GetProperty("area_sq_km").ValueKind.Should().Be(JsonValueKind.Number);
                row.GetProperty("area_sq_km").GetDouble().Should().Be(expected[index].Item4);
            }

            await using var persisted = new NpgsqlCommand($$"""
                SELECT objectid, attributes->>'population', jsonb_typeof(attributes->'population'),
                    attributes->>'area_sq_km', jsonb_typeof(attributes->'area_sq_km')
                FROM {{schema}}.features WHERE objectid >= 73901 ORDER BY objectid;
                """, connection);
            await using var reader = await persisted.ExecuteReaderAsync();
            var count = 0;
            while (await reader.ReadAsync())
            {
                reader.GetInt64(0).Should().Be(expected[count].Item1);
                long.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture).Should().Be(expected[count].Item3);
                reader.GetString(2).Should().Be("number");
                double.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture).Should().Be(expected[count].Item4);
                reader.GetString(4).Should().Be("number");
                count++;
            }
            count.Should().Be(3);
        }

        using var calculated = await CalculateAsync("""
            [{"field":"population","sqlExpression":"population * 3 + 2"},
             {"field":"area_sq_km","sqlExpression":"area_sq_km / 2 + 0.125"}]
            """);
        calculated.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        calculated.RootElement.GetProperty("updatedFeatureCount").GetInt32().Should().Be(2);
        await AssertRowsAsync();

        using var invalid = await CalculateAsync("""
            [{"field":"population","sqlExpression":"population + 100"},
             {"field":"area_sq_km","sqlExpression":"area_sq_km / (population - 35)"}]
            """);
        invalid.RootElement.TryGetProperty("error", out _).Should().BeTrue("the second selected row divides by zero");
        await AssertRowsAsync();
    }
}
