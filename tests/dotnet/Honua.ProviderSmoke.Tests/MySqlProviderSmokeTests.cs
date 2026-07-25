// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.ProviderSmoke.Tests;

/// <summary>
/// Interface-level HTTP-stack smoke coverage for the MySql/MariaDB provider
/// (honua-server#2947). Boots a real ASP.NET Core host with
/// <c>DataSource:Provider=mysql</c> against a Testcontainers <c>mysql:8</c> instance. See
/// <see cref="PrimaryProviderSmokeTestsBase"/> for the shared assertions.
/// </summary>
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
    [Protocol(ProtocolNames.ODataV4)]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task OData_CreateFeature_ReadOnlyProvider_ReturnsProviderWriteNotSupported()
    {
        using var content = JsonContent.Create(new
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Must not persist",
                ["area"] = 999.0,
                ["type"] = "commercial"
            }
        });

        var response = await Client.PostAsync(
            $"/odata/Layers({ProviderSmokeGraph.LayerId})/Features",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented, await response.Content.ReadAsStringAsync());
        using var errorDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        errorDocument.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("ProviderWriteNotSupported");

        var queryResponse = await Client.GetAsync($"/odata/Features({ProviderSmokeGraph.LayerId})");
        queryResponse.StatusCode.Should().Be(HttpStatusCode.OK, await queryResponse.Content.ReadAsStringAsync());
        using var queryDocument = JsonDocument.Parse(await queryResponse.Content.ReadAsStringAsync());
        queryDocument.RootElement.GetProperty("value").GetArrayLength().Should().Be(ProviderSmokeData.Parcels.Count);
    }
}
