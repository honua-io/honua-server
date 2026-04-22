// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.OData.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OData;

[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public sealed class ODataAuthorizationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-odata-admin-key");
        });

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task CreateFeature_WithoutApiKey_ReturnsUnauthorized()
    {
        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Unauthorized Create"
            }
        };

        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        var response = await _fixture.Client.PostAsync(
            "/odata/Layers(0)/Features",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task UpdateFeature_WithoutApiKey_ReturnsUnauthorized()
    {
        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Unauthorized Update"
            }
        };

        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        var message = new HttpRequestMessage(new HttpMethod("PATCH"), "/odata/Features(LayerId=0,ObjectId=1)")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var response = await _fixture.Client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task DeleteFeature_WithoutApiKey_ReturnsUnauthorized()
    {
        var response = await _fixture.Client.DeleteAsync("/odata/Features(LayerId=0,ObjectId=1)");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
