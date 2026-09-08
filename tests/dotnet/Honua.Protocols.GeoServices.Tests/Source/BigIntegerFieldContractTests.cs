// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

/// <summary>
/// Protects the Esri BigInteger wire contract discovered during native desktop testing.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer, TestProtocols.MapServer)]
public sealed class BigIntegerFieldContractTests
{
    [IntegrationTheory]
    [InlineData("FeatureServer/0")]
    [InlineData("MapServer/0")]
    [InlineData("FeatureServer/0/query", true)]
    [Operation(Operations.GetMetadata, Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Get_BigIntegerField_UsesEsriTypeAndPreservesValues(string path, bool query = false)
    {
        var fixture = new WebAppFixture();
        await fixture.InitializeAsync();
        try
        {
            fixture.UpdateV2ResourceSchemaField(0, new MetadataV2Field
            {
                Name = "large_int",
                Type = MetadataV2FieldType.BigInteger
            });
            await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema!);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO features (layer_id, geometry, attributes)
                VALUES (0, NULL, '{"name":"big-integer-contract","large_int":9007199254740991}'),
                       (0, NULL, '{"name":"big-integer-contract","large_int":-9007199254740991}'),
                       (0, NULL, '{"name":"big-integer-contract","large_int":2147483648}'),
                       (0, NULL, '{"name":"big-integer-contract","large_int":null}');
                """;
            await command.ExecuteNonQueryAsync();

            var queryParameters = query
                ? "&where=name%3D%27big-integer-contract%27&outFields=*&returnGeometry=false&orderByFields=objectid"
                : string.Empty;
            using var response = await fixture.Client.GetAsync($"/rest/services/test/{path}?f=json{queryParameters}");
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            using var document = JsonDocument.Parse(body);
            var fields = document.RootElement.GetProperty("fields").EnumerateArray().ToArray();
            fields.Single(field => field.GetProperty("name").GetString() == "large_int")
                .GetProperty("type").GetString().Should().Be("esriFieldTypeBigInteger");
            fields.Single(field => field.GetProperty("name").GetString() == "objectid")
                .GetProperty("type").GetString().Should().Be("esriFieldTypeOID");
            if (query)
            {
                document.RootElement.GetProperty("features").EnumerateArray()
                    .Select(feature => feature.GetProperty("attributes").GetProperty("large_int"))
                    .Select(value => value.ValueKind == JsonValueKind.Null ? (long?)null : value.GetInt64())
                    .Should().Equal(9007199254740991L, -9007199254740991L, 2147483648L, null);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
