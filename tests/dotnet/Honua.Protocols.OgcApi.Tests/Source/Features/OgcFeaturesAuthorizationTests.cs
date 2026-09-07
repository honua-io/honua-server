// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

/// <summary>
/// OGC API Features is a GA write surface, and the promise on it is that an
/// unauthenticated caller is refused <em>and changes nothing</em> — and, on the read side,
/// learns nothing. A status code alone proves neither: a handler that refuses the response
/// after committing the edit, or one that leaks the record into the error body, returns the
/// same 401. Every denial below therefore reads the database directly (bypassing the server's
/// own read path, and therefore any cache or projection) before and after the refused call.
/// </summary>
[Protocol(TestProtocols.OgcApiFeatures)]
[Collection("Database")]
public sealed class OgcFeaturesAuthorizationTests : IClassFixture<OgcFeaturesAuthorizationTestsFixture>
{
    private const string AdminApiKey = "test-ogc-admin-key";

    // Seeded layer 0, objectid 1 (tests/seed/server.yaml). The denial cases target it, so
    // its stored values are both the read-back oracle and the strings a refusal body must
    // not disclose.
    private const long SeededFeatureId = 1;
    private const string SeededFeatureName = "Test Feature";
    private const string SeededFeatureDescription = "A test feature for integration tests";

    private readonly WebAppFixture _fixture;

    public OgcFeaturesAuthorizationTests(OgcFeaturesAuthorizationTestsFixture fixture)
    {
        _fixture = fixture.App;
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WithoutApiKey_ReturnsUnauthorized()
    {
        const string name = "Unauthorized Create";
        var countBefore = await _fixture.CountStoredFeaturesAsync(WebAppFixture.TestLayerId);

        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items",
            CreateGeoJsonContent(name));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertBodyDisclosesNoRecordData(await response.Content.ReadAsStringAsync());

        (await _fixture.CountStoredFeaturesAsync(WebAppFixture.TestLayerId)).Should().Be(countBefore);
        (await _fixture.CountStoredFeaturesByNameAsync(WebAppFixture.TestLayerId, name)).Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task UpdateFeature_WithoutApiKey_ReturnsUnauthorized()
    {
        const string name = "Unauthorized Update";
        var countBefore = await _fixture.CountStoredFeaturesAsync(WebAppFixture.TestLayerId);

        var response = await _fixture.Client.PutAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items/{SeededFeatureId}",
            CreateGeoJsonContent(name));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertBodyDisclosesNoRecordData(await response.Content.ReadAsStringAsync());

        // The targeted row still holds its seeded name, so the refusal happened before the
        // edit rather than after it.
        (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, SeededFeatureId))
            .Should().Be(SeededFeatureName);
        (await _fixture.CountStoredFeaturesByNameAsync(WebAppFixture.TestLayerId, name)).Should().Be(0);
        (await _fixture.CountStoredFeaturesAsync(WebAppFixture.TestLayerId)).Should().Be(countBefore);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task DeleteFeature_WithoutApiKey_ReturnsUnauthorized()
    {
        var countBefore = await _fixture.CountStoredFeaturesAsync(WebAppFixture.TestLayerId);

        var response = await _fixture.Client.DeleteAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items/{SeededFeatureId}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertBodyDisclosesNoRecordData(await response.Content.ReadAsStringAsync());

        (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, SeededFeatureId))
            .Should().Be(SeededFeatureName);
        (await _fixture.CountStoredFeaturesAsync(WebAppFixture.TestLayerId)).Should().Be(countBefore);
    }

    [IntegrationTest]
    [Operation(Operations.BulkCreate)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items/batch")]
    public async Task Batch_WithoutApiKey_ReturnsUnauthorized()
    {
        const string name = "Unauthorized Batch";
        var countBefore = await _fixture.CountStoredFeaturesAsync(WebAppFixture.TestLayerId);

        var batch = new BatchRequest
        {
            Operations =
            [
                new BatchOperation
                {
                    Id = "create-1",
                    Type = "CREATE",
                    Feature = CreatePointFeature(name)
                },
                new BatchOperation
                {
                    Id = "delete-1",
                    Type = "DELETE",
                    FeatureId = SeededFeatureId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }
            ]
        };

        var content = JsonSerializer.Serialize(batch, OgcJsonContext.Default.BatchRequest);
        using var requestContent = new StringContent(content, Encoding.UTF8, MediaTypes.Json);
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items/batch",
            requestContent);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertBodyDisclosesNoRecordData(await response.Content.ReadAsStringAsync());

        // Neither half of the batch landed: nothing was inserted and the delete target is
        // still there. A per-operation partial commit would move one of these two counts.
        (await _fixture.CountStoredFeaturesByNameAsync(WebAppFixture.TestLayerId, name)).Should().Be(0);
        (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, SeededFeatureId))
            .Should().Be(SeededFeatureName);
        (await _fixture.CountStoredFeaturesAsync(WebAppFixture.TestLayerId)).Should().Be(countBefore);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithoutApiKey_IsRefusedAndDisclosesNoRecords()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, body);

        // Refusal is only half the promise. The body must not carry the collection either,
        // so a handler that renders the FeatureCollection and then stamps a 401 on it fails.
        AssertBodyDisclosesNoRecordData(body);
        body.Should().NotContain("FeatureCollection");
        body.Should().NotContain("\"features\"");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task GetItem_WithoutApiKey_IsRefusedAndDisclosesNoRecord()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items/{SeededFeatureId}");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, body);
        AssertBodyDisclosesNoRecordData(body);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithApiKey_ReturnsSeededRecords()
    {
        // The paired success case. Without it the denial tests above would still pass if the
        // read endpoint were broken for everyone.
        using var client = _fixture.CreateClient(c =>
            c.DefaultRequestHeaders.Add("X-API-Key", AdminApiKey));

        var response = await client.GetAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items?ids={SeededFeatureId}");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var feature = JsonDocument.Parse(body).RootElement
            .GetProperty("features").EnumerateArray().Single();
        feature.GetProperty("id").GetInt64().Should().Be(SeededFeatureId);
        feature.GetProperty("properties").GetProperty("name").GetString().Should().Be(SeededFeatureName);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WithApiKey_ReturnsCreated()
    {
        const string name = "Authorized Create";
        using var client = _fixture.CreateClient(c =>
            c.DefaultRequestHeaders.Add("X-API-Key", AdminApiKey));

        var response = await client.PostAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items",
            CreateGeoJsonContent(name));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);

        var created = JsonSerializer.Deserialize(body, OgcJsonContext.Default.GeoJsonFeature);
        created.Should().NotBeNull();
        var createdId = ReadFeatureId(created!.Id);
        createdId.Should().NotBeNull();

        // A 201 says the request was accepted, not that the row exists with the submitted
        // values. Read it back over the protocol and out of Postgres.
        var readBack = await client.GetAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items/{createdId!.Value}");
        var readBackBody = await readBack.Content.ReadAsStringAsync();
        readBack.StatusCode.Should().Be(HttpStatusCode.OK, readBackBody);

        var readBackFeature = JsonDocument.Parse(readBackBody).RootElement;
        readBackFeature.GetProperty("properties").GetProperty("name").GetString().Should().Be(name);
        var coordinates = readBackFeature.GetProperty("geometry").GetProperty("coordinates")
            .EnumerateArray().Select(value => value.GetDouble()).ToArray();
        coordinates[0].Should().BeApproximately(-122.4194, 1e-6);
        coordinates[1].Should().BeApproximately(37.7749, 1e-6);

        (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, createdId.Value))
            .Should().Be(name);
    }

    private static long? ReadFeatureId(object? id)
        => id switch
        {
            long longId => longId,
            int intId => intId,
            string text when long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedText) => parsedText,
            JsonElement { ValueKind: JsonValueKind.Number } number when number.TryGetInt64(out var parsedNumber) => parsedNumber,
            JsonElement { ValueKind: JsonValueKind.String } jsonText
                when long.TryParse(jsonText.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedJsonText) => parsedJsonText,
            _ => null
        };

    /// <summary>
    /// A refusal must not become a disclosure channel. These are the stored values of the
    /// seeded row every denial case above targets; none may appear in the response body.
    /// </summary>
    private static void AssertBodyDisclosesNoRecordData(string body)
    {
        body.Should().NotContain(SeededFeatureName);
        body.Should().NotContain(SeededFeatureDescription);
        body.Should().NotContain("-122.5");
    }

    private static StringContent CreateGeoJsonContent(string name)
    {
        var feature = CreatePointFeature(name);
        var json = JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature);
        return new StringContent(json, Encoding.UTF8, MediaTypes.GeoJson);
    }

    private static GeoJsonFeature CreatePointFeature(string name)
    {
        return new GeoJsonFeature
        {
            Type = "Feature",
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.4194, 37.7749]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = name
            }
        };
    }
}

public sealed class OgcFeaturesAuthorizationTestsFixture : IAsyncLifetime
{
    private const string AdminApiKey = "test-ogc-admin-key";

    public WebAppFixture App { get; } = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            // The dev-auth bypass authenticates every request as `dev-bypass` before a
            // credential is read, which is what makes most protocol test projects unable to
            // reach a denial at all. It is off here as well as dev auth itself, so an
            // anonymous request stays anonymous.
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminApiKey);
        });

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();

        // Deny anonymous read on the test layer so the unauthenticated GET cases have a
        // policy to be refused by, rather than depending on whatever the default happens
        // to be. The write cases are refused by authentication and are unaffected.
        App.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false });
    }

    public Task DisposeAsync() => App.DisposeAsync();
}
