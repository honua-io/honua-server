// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;

namespace Honua.ProviderSmoke.Tests;

/// <summary>
/// Interface-level HTTP-stack smoke coverage for the MySql/MariaDB provider
/// (honua-server#2947). Boots a real ASP.NET Core host with
/// <c>DataSource:Provider=mysql</c> against a Testcontainers <c>mysql:8</c> instance. See
/// <see cref="PrimaryProviderSmokeTestsBase"/> for the shared read assertions, including
/// the bbox and raster-tile cases that were skipped here until the honua-server#2965
/// EWKB/plain-WKB converter fix landed.
/// </summary>
/// <remarks>
/// MySQL/MariaDB is a read/query-only provider by design (docs/reference/configuration/
/// data-sources): <c>MySqlFeatureStore.Writer</c> is <see langword="null"/>, its
/// capabilities are <c>FeatureProviderCapabilities.ReadOnlyMySql</c>, and DI registers a
/// <c>ReadOnlyFeatureWriter</c> that rejects every write with
/// <see cref="NotSupportedException"/>. The edit round-trip test here therefore proves the
/// documented rejection posture — a clean 405 MethodNotAllowed through the shared
/// exception mapping (Esri-style: HTTP 200 with an <c>error.code</c> body), never the
/// generic 500 — and that a rejected edit batch leaves the seeded data untouched.
/// </remarks>
[Trait("Provider", "MySql")]
public sealed class MySqlProviderSmokeTests : PrimaryProviderSmokeTestsBase, IClassFixture<MySqlProviderWebAppFixture>
{
    private readonly MySqlProviderWebAppFixture _fixture;

    public MySqlProviderSmokeTests(MySqlProviderWebAppFixture fixture)
    {
        _fixture = fixture;
    }

    protected override HttpClient Client => _fixture.Client;

    [IntegrationTest]
    [Protocol(ProtocolNames.FeatureServer)]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task FeatureServer_ApplyEdits_ReadOnlyProvider_Returns405AndLeavesDataUnchanged()
    {
        // Create + update + delete in one canonical edit batch. The MySql provider is
        // read/query-only by design, so the shared edit pipeline must reject the batch
        // with the documented clean 405 (NotSupportedException -> shared exception
        // mapping), not a generic 500, and must not partially apply anything.
        var payload = /*lang=json,strict*/ """
            {
              "adds": [
                {
                  "attributes": { "name": "Should Not Exist", "area": 1.5, "type": "commercial" },
                  "geometry": { "x": -122.36, "y": 37.81 }
                }
              ],
              "updates": [
                {
                  "attributes": { "objectid": 1, "name": "Should Not Change" }
                }
              ],
              "deletes": [2]
            }
            """;

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/rest/services/{ProviderSmokeGraph.ServiceName}/FeatureServer/{ProviderSmokeGraph.LayerId}/applyEdits")
        {
            Content = content,
        };
        // Authenticate as admin (the fixture host runs with the Test-environment dev-auth
        // bypass, where any X-API-Key authenticates) so the request clears the shared RBAC
        // write gate and the rejection under test is the provider's read-only posture, not
        // an auth denial.
        request.Headers.Add("X-API-Key", "provider-smoke-admin");
        var response = await Client.SendAsync(request);

        // GeoServices REST errors are signalled Esri-style: HTTP 200 with an
        // {"error":{"code":...}} body (PA-070/PA-117). The read-only provider's
        // NotSupportedException maps through the shared exception mapping to the 405
        // MethodNotAllowed body code — the documented rejection — never the generic 500
        // InternalServerError code.
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var errorDocument = JsonDocument.Parse(body);
        errorDocument.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(405, body);
        // The shared mapping's sanitized detail — no provider internals, SQL, or stack traces.
        body.Should().NotContainAny("Exception", "MySqlConnector", "stack");

        // Round-trip proof: the rejected batch changed nothing — all 5 seeded rows are
        // still present, parcel 1 is unrenamed, and parcel 2 is undeleted.
        var queryResponse = await Client.GetAsync(
            $"/rest/services/{ProviderSmokeGraph.ServiceName}/FeatureServer/{ProviderSmokeGraph.LayerId}/query" +
            "?where=1%3D1&outFields=*&f=json");
        queryResponse.StatusCode.Should().Be(HttpStatusCode.OK, await queryResponse.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await queryResponse.Content.ReadAsStringAsync());
        var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();
        features.Should().HaveCount(ProviderSmokeData.Parcels.Count);

        var byId = features.ToDictionary(
            f => f.GetProperty("attributes").GetProperty("objectid").GetInt64(),
            f => f.GetProperty("attributes"));
        byId.Should().ContainKey(1L).WhoseValue.GetProperty("name").GetString().Should().Be("Parcel 1");
        byId.Should().ContainKey(2L);
    }
}
