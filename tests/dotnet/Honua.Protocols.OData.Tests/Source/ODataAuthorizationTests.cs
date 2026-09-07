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

/// <summary>
/// OData v4 is a GA write surface, and the promise on it is that an unauthenticated caller
/// is refused <em>and changes nothing</em> — and, on the read side, learns nothing. A 401
/// alone proves neither: a handler that commits the edit and then refuses the response, and
/// one that renders the record into its error body, both return 401. Every denial below
/// therefore reads Postgres directly, around the server's own read path, before and after
/// the refused call.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataAuthorizationTests : IAsyncLifetime
{
    private const string AdminApiKey = "test-odata-admin-key";
    private const int TestLayerId = 0;

    // Seeded layer 0, objectid 1 (tests/seed/odata.yaml). The mutation denials target it, so
    // its stored values are both the read-back oracle and the strings a refusal must not
    // disclose.
    private const long SeededFeatureId = 1;
    private const string SeededFeatureName = "San Francisco";
    private const string SeededFeatureNote = "Heart of Silicon Valley";

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            // The dev-auth bypass authenticates every request as `dev-bypass` before a
            // credential is read. Both it and dev auth are off here, so an anonymous
            // request stays anonymous and the denial paths are reachable.
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminApiKey);
        });

    public async Task InitializeAsync()
    {
        // All segments are relative literal path fragments (not user input), so none can be
        // rooted and silently drop earlier arguments.
        _fixture.UseSeed(Path.Join("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();

        // Deny anonymous read on the layer so the unauthenticated GET cases are refused by
        // an explicit policy rather than by whatever the default happens to be. The write
        // cases are refused by authentication and are unaffected.
        _fixture.UpdateV2ResourceMetadata(
            TestLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false });
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task CreateFeature_WithoutApiKey_ReturnsUnauthorized()
    {
        const string name = "Unauthorized Create";
        var countBefore = await _fixture.CountStoredFeaturesAsync(TestLayerId);

        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = name
            }
        };

        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync("/odata/Layers(0)/Features", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertBodyDisclosesNoRecordData(await response.Content.ReadAsStringAsync());

        (await _fixture.CountStoredFeaturesByNameAsync(TestLayerId, name)).Should().Be(0);
        (await _fixture.CountStoredFeaturesAsync(TestLayerId)).Should().Be(countBefore);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task CreateFeature_WithMalformedJsonWithoutApiKey_ReturnsUnauthorizedBeforeBodyValidation()
    {
        var countBefore = await _fixture.CountStoredFeaturesAsync(TestLayerId);

        using var content = new StringContent("{", Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync("/odata/Layers(0)/Features", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertBodyDisclosesNoRecordData(await response.Content.ReadAsStringAsync());

        (await _fixture.CountStoredFeaturesAsync(TestLayerId)).Should().Be(countBefore);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task UpdateFeature_WithoutApiKey_ReturnsUnauthorized()
    {
        const string name = "Unauthorized Update";
        var countBefore = await _fixture.CountStoredFeaturesAsync(TestLayerId);

        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = name
            }
        };

        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        using var message = new HttpRequestMessage(new HttpMethod("PATCH"), "/odata/Features(LayerId=0,ObjectId=1)")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var response = await _fixture.Client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertBodyDisclosesNoRecordData(await response.Content.ReadAsStringAsync());

        // The targeted row still holds its seeded name, so the refusal preceded the edit.
        (await _fixture.ReadStoredFeatureNameAsync(TestLayerId, SeededFeatureId))
            .Should().Be(SeededFeatureName);
        (await _fixture.CountStoredFeaturesByNameAsync(TestLayerId, name)).Should().Be(0);
        (await _fixture.CountStoredFeaturesAsync(TestLayerId)).Should().Be(countBefore);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task UpdateFeature_WithMalformedJsonWithoutApiKey_ReturnsUnauthorizedBeforeBodyValidation()
    {
        var countBefore = await _fixture.CountStoredFeaturesAsync(TestLayerId);

        using var message = new HttpRequestMessage(new HttpMethod("PATCH"), "/odata/Features(LayerId=0,ObjectId=1)")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json")
        };

        var response = await _fixture.Client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertBodyDisclosesNoRecordData(await response.Content.ReadAsStringAsync());

        (await _fixture.ReadStoredFeatureNameAsync(TestLayerId, SeededFeatureId))
            .Should().Be(SeededFeatureName);
        (await _fixture.CountStoredFeaturesAsync(TestLayerId)).Should().Be(countBefore);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task DeleteFeature_WithoutApiKey_ReturnsUnauthorized()
    {
        var countBefore = await _fixture.CountStoredFeaturesAsync(TestLayerId);

        var response = await _fixture.Client.DeleteAsync("/odata/Features(LayerId=0,ObjectId=1)");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertBodyDisclosesNoRecordData(await response.Content.ReadAsStringAsync());

        (await _fixture.ReadStoredFeatureNameAsync(TestLayerId, SeededFeatureId))
            .Should().Be(SeededFeatureName);
        (await _fixture.CountStoredFeaturesAsync(TestLayerId)).Should().Be(countBefore);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers({layerId})/Features")]
    public async Task GetFeatures_WithoutApiKey_IsRefusedAndDisclosesNoRecords()
    {
        var response = await _fixture.Client.GetAsync("/odata/Layers(0)/Features");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, body);

        // Refusal is only half the promise: the body must not carry the rows either.
        AssertBodyDisclosesNoRecordData(body);
        AssertBodyCarriesNoResultSet(body);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task GetFeature_WithoutApiKey_IsRefusedAndDisclosesNoRecord()
    {
        var response = await _fixture.Client.GetAsync("/odata/Features(LayerId=0,ObjectId=1)");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, body);
        AssertBodyDisclosesNoRecordData(body);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers({layerId})/Features")]
    public async Task GetFeatures_WithApiKey_ReturnsSeededRecords()
    {
        // The paired success case: without it, the denials above would still pass if the
        // read endpoint were broken for every principal.
        using var client = _fixture.CreateClient(c =>
            c.DefaultRequestHeaders.Add("X-API-Key", AdminApiKey));

        var response = await client.GetAsync("/odata/Layers(0)/Features?$filter=ObjectId%20eq%201");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var row = JsonDocument.Parse(body).RootElement.GetProperty("value").EnumerateArray().Single();
        ODataTestHelpers.ParseAttributes(row).GetProperty("name").GetString().Should().Be(SeededFeatureName);
    }

    /// <summary>
    /// A refusal must not become a disclosure channel. These are stored values of the seeded
    /// rows the denial cases target or would have returned; none may appear in the body.
    /// </summary>
    private static void AssertBodyDisclosesNoRecordData(string body)
    {
        body.Should().NotContain(SeededFeatureName);
        body.Should().NotContain(SeededFeatureNote);
        body.Should().NotContain("874961");
    }

    private static void AssertBodyCarriesNoResultSet(string body)
    {
        body.Should().NotContain("\"value\"");
        body.Should().NotContain("Attributes");
    }
}
