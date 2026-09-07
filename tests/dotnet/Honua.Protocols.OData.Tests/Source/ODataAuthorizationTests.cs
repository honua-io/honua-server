// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.OData.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.OData;

[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataAuthorizationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-odata-admin-key");
        });

    public async Task InitializeAsync()
    {
        // All segments are relative literal path fragments (not user input), so none can be
        // rooted and silently drop earlier arguments.
        _fixture.UseSeed(Path.Join("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
        _fixture.UpdateV2ResourceMetadata(0, accessPolicy: new Honua.Core.Features.Security.Domain.AccessPolicy
        {
            AllowAnonymous = false,
            AllowAnonymousWrite = false
        });
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task CreateFeature_WithoutApiKey_ReturnsUnauthorized()
    {
        var before = await ReadAuthorizedStateAsync();
        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Unauthorized Create"
            }
        };

        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync("/odata/Layers(0)/Features", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertDeniedWithoutChangesAsync(response, before);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task CreateFeature_WithMalformedJsonWithoutApiKey_ReturnsUnauthorizedBeforeBodyValidation()
    {
        var before = await ReadAuthorizedStateAsync();
        using var content = new StringContent("{", Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync("/odata/Layers(0)/Features", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertDeniedWithoutChangesAsync(response, before);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task UpdateFeature_WithoutApiKey_ReturnsUnauthorized()
    {
        var before = await ReadAuthorizedStateAsync();
        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Unauthorized Update"
            }
        };

        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        using var message = new HttpRequestMessage(new HttpMethod("PATCH"), "/odata/Features(LayerId=0,ObjectId=1)")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var response = await _fixture.Client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertDeniedWithoutChangesAsync(response, before);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task UpdateFeature_WithMalformedJsonWithoutApiKey_ReturnsUnauthorizedBeforeBodyValidation()
    {
        var before = await ReadAuthorizedStateAsync();
        using var message = new HttpRequestMessage(new HttpMethod("PATCH"), "/odata/Features(LayerId=0,ObjectId=1)")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json")
        };

        var response = await _fixture.Client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertDeniedWithoutChangesAsync(response, before);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task DeleteFeature_WithoutApiKey_ReturnsUnauthorized()
    {
        var before = await ReadAuthorizedStateAsync();
        var response = await _fixture.Client.DeleteAsync("/odata/Features(LayerId=0,ObjectId=1)");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertDeniedWithoutChangesAsync(response, before);
    }

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /odata/Layers({layerId})/Features")]
    public async Task GetFeatures_WithoutApiKey_RefusesAndDisclosesNoRecords()
    {
        var before = await ReadAuthorizedStateAsync();
        using var response = await _fixture.Client.GetAsync("/odata/Layers(0)/Features");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertDeniedWithoutChangesAsync(response, before);
    }

    private async Task<(int Count, string Target)> ReadAuthorizedStateAsync()
    {
        using var client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", "test-odata-admin-key"));
        using var list = await client.GetAsync("/odata/Layers(0)/Features?$top=1000");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var collection = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var count = collection.RootElement.GetProperty("value").GetArrayLength();
        // tests/seed/odata.yaml seeds objectids 1-15 on layer 0.
        count.Should().Be(15, "all fifteen seeded cities must be present before and after a denial");
        using var response = await client.GetAsync("/odata/Features(LayerId=0,ObjectId=1)");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var target = await response.Content.ReadAsStringAsync();
        target.Should().Contain("San Francisco");
        return (count, target);
    }

    private async Task AssertDeniedWithoutChangesAsync(HttpResponseMessage response, (int Count, string Target) before)
    {
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("San Francisco").And.NotContain("874961")
            .And.NotContain("Unauthorized Create").And.NotContain("Unauthorized Update");
        using var error = JsonDocument.Parse(body);
        error.RootElement.TryGetProperty("value", out _).Should().BeFalse();
        error.RootElement.TryGetProperty("Attributes", out _).Should().BeFalse();
        (await ReadAuthorizedStateAsync()).Should().Be(before, "a denied request must preserve row count and the complete target feature");
    }
}
