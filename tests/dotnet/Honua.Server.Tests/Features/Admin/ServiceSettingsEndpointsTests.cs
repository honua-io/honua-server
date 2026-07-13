// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class ServiceSettingsEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "service-settings-admin-key";
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ServiceSettingsEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services")]
    public async Task ListServices_WithAdminAuth_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/v1/admin/services");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services")]
    public async Task ListServices_ResponseIncludesLayerCountAndEnabledProtocols()
    {
        var response = await _client.GetAsync("/api/v1/admin/services");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var data = document.RootElement.GetProperty("data");

        data.GetArrayLength().Should().BeGreaterThan(0);
        var first = data[0];
        first.TryGetProperty("layerCount", out _).Should().BeTrue();
        first.TryGetProperty("enabledProtocols", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/settings")]
    public async Task GetServiceSettings_WithServiceName_ReturnsSettingsOrNotFound()
    {
        var response = await _client.GetAsync("/api/v1/admin/services/test/settings");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/settings")]
    public async Task GetServiceSettings_ResponseIncludesAccessPolicyAndTimeInfo()
    {
        var response = await _client.GetAsync("/api/v1/admin/services/test/settings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var data = document.RootElement.GetProperty("data");

        // accessPolicy and timeInfo should be present as properties (may be null)
        data.TryGetProperty("accessPolicy", out _).Should().BeTrue("response should include accessPolicy field");
        data.TryGetProperty("timeInfo", out _).Should().BeTrue("response should include timeInfo field");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/settings")]
    public async Task GetServiceSettings_WithDefaultMetadata_IncludesGrpcInEnabledAndAvailableProtocols()
    {
        var response = await _client.GetAsync("/api/v1/admin/services/test/settings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var data = document.RootElement.GetProperty("data");

        var enabledProtocols = data.GetProperty("enabledProtocols")
            .EnumerateArray()
            .Select(protocol => protocol.GetString())
            .OfType<string>()
            .ToArray();

        var availableProtocols = data.GetProperty("availableProtocols")
            .EnumerateArray()
            .Select(protocol => protocol.GetString())
            .OfType<string>()
            .ToArray();

        enabledProtocols.Should().Contain("Grpc");
        availableProtocols.Should().Contain("Grpc");
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/protocols")]
    public async Task UpdateProtocols_WithValidPayload_ReturnsUpdatedOrNotFound()
    {
        var body = """
            {
              "enabledProtocols": ["FeatureServer", "MapServer", "OgcFeatures", "OData"]
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/api/v1/admin/services/test/protocols", content);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/protocols")]
    public async Task UpdateProtocols_WithEmptyArray_Returns400()
    {
        var body = """
            {
              "enabledProtocols": []
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/api/v1/admin/services/test/protocols", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("At least one protocol");
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/mapserver")]
    public async Task UpdateMapServerSettings_WithValidPayload_ReturnsNotImplemented()
    {
        var payload = JsonSerializer.Serialize(new
        {
            maxImageWidth = 4096,
            maxImageHeight = 4096,
            defaultFormat = "png",
            defaultTransparent = true
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/api/v1/admin/services/test/mapserver", content);

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/access-policy")]
    public async Task UpdateAccessPolicy_WithValidPayload_ReturnsUpdatedOrNotFound()
    {
        var body = """
            {
              "allowAnonymous": true
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/api/v1/admin/services/test/access-policy", content);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/timeinfo")]
    public async Task UpdateTimeInfo_WithValidPayload_ReturnsNotImplemented()
    {
        var body = """
            {
              "startTimeField": "created_at"
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/api/v1/admin/services/test/timeinfo", content);

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    public async Task UpdateLayerMetadata_WithValidPayload_ReturnsUpdatedOrNotFound()
    {
        var body = """
            {
              "accessPolicy": {
                "allowAnonymous": true
              }
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/api/v1/admin/services/test/layers/1/metadata", content);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    public async Task UpdateLayerMetadata_WithTimeInfoPayload_ReturnsOkAndRoundTrips()
    {
        // Regression for honua-io/honua-server#1910 symptom 3: PUTting a timeInfo block
        // on a layer must succeed (HTTP 200) and round-trip the configured fields rather
        // than 500ing with a NullReferenceException. The typed V2 temporal write path
        // (ToV2Temporal -> MutateResourcesForLayerAsync) must null-guard every slot it
        // consumes.
        var body = """
            {
              "timeInfo": {
                "startTimeField": "timestamp",
                "endTimeField": "event_date"
              }
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/api/v1/admin/services/test/layers/0/metadata", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var data = document.RootElement.GetProperty("data");

        var timeInfo = data.GetProperty("timeInfo");
        timeInfo.ValueKind.Should().Be(JsonValueKind.Object);
        timeInfo.GetProperty("startTimeField").GetString().Should().Be("timestamp");
        timeInfo.GetProperty("endTimeField").GetString().Should().Be("event_date");
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    public async Task UpdateLayerMetadata_WithEmptyTimeInfo_ClearsTemporalWithoutError()
    {
        // Companion to the timeInfo round-trip: an empty-field timeInfo clears the
        // temporal slot (returns it as null) and must not NRE on the clear path.
        //
        // Layer 0 is seeded temporal (startTimeField=timestamp, endTimeField=event_date).
        // The update is a partial patch: clearing only startTimeField would retain the
        // seeded endTimeField and leave the slot non-null, so clear every temporal field
        // to drive the slot to null and exercise the clear-to-null path.
        var setBody = """
            {
              "timeInfo": {
                "startTimeField": "timestamp"
              }
            }
            """;
        using (var setContent = new StringContent(setBody, Encoding.UTF8, "application/json"))
        {
            await _client.PutAsync(
                "/api/v1/admin/services/test/layers/0/metadata",
                setContent);
        }

        var clearBody = """
            {
              "timeInfo": {
                "startTimeField": "",
                "endTimeField": "",
                "trackIdField": ""
              }
            }
            """;
        using var clearContent = new StringContent(clearBody, Encoding.UTF8, "application/json");
        var response = await _client.PutAsync(
            "/api/v1/admin/services/test/layers/0/metadata",
            clearContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var data = document.RootElement.GetProperty("data");

        if (data.TryGetProperty("timeInfo", out var timeInfo))
        {
            timeInfo.ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    public async Task UpdateLayerMetadata_WithRasterMosaicPayload_ReturnsUpdatedMergeStrategy()
    {
        var body = """
            {
              "rasterMosaic": {
                "mergeStrategy": "max"
              }
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/api/v1/admin/services/test/layers/1/metadata", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var data = document.RootElement.GetProperty("data");

        data.GetProperty("rasterMosaic").GetProperty("mergeStrategy").GetString().Should().Be("max");
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    public async Task UpdateLayerMetadata_WithMixedCaseMergeStrategy_NormalizesToCanonical()
    {
        var body = """
            {
              "rasterMosaic": {
                "mergeStrategy": "Average"
              }
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/api/v1/admin/services/test/layers/1/metadata", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var data = document.RootElement.GetProperty("data");

        data.GetProperty("rasterMosaic").GetProperty("mergeStrategy").GetString().Should().Be("average");
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    public async Task UpdateLayerMetadata_WithUnknownMergeStrategy_ReturnsBadRequest()
    {
        var body = """
            {
              "rasterMosaic": {
                "mergeStrategy": "averagge"
              }
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/api/v1/admin/services/test/layers/1/metadata", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("newest").And.Contain("oldest").And.Contain("average").And.Contain("max").And.Contain("min");
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/protocols")]
    [Endpoint("GET /rest/services/{serviceName}/FeatureServer")]
    public async Task UpdateProtocols_DisableFeatureServer_BlocksFeatureServerServiceMetadata()
    {
        var body = """
            {
              "enabledProtocols": ["MapServer", "OgcFeatures", "OData", "Grpc"]
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var updateResponse = await _client.PutAsync("/api/v1/admin/services/test/protocols", content);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var featureServerResponse = await _fixture.Client.GetAsync("/rest/services/test/FeatureServer?f=json");
        await featureServerResponse.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/protocols")]
    [Endpoint("GET /rest/services/{serviceName}/FeatureServer/{layerId}")]
    public async Task UpdateProtocols_DisableFeatureServer_BlocksFeatureServerLayerMetadata()
    {
        var body = """
            {
              "enabledProtocols": ["MapServer", "OgcFeatures", "OData", "Grpc"]
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var updateResponse = await _client.PutAsync("/api/v1/admin/services/test/protocols", content);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var layerMetadataResponse = await _fixture.Client.GetAsync("/rest/services/test/FeatureServer/1?f=json");
        // PA-070/PA-117 (#2418): a blocked GeoServices surface reports HTTP 200 + {"error":{"code":404}}
        await layerMetadataResponse.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/protocols")]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task UpdateProtocols_DisableFeatureServer_BlocksLayerTiles()
    {
        var body = """
            {
              "enabledProtocols": ["MapServer", "OgcFeatures", "OData", "Grpc"]
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var updateResponse = await _client.PutAsync("/api/v1/admin/services/test/protocols", content);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var tileResponse = await _client.GetAsync("/tiles/1/0/0/0.mvt");
        // /tiles is GeoServices-classified: blocked tiles report HTTP 200 + {"error":{"code":404}}
        await tileResponse.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/protocols")]
    [Endpoint("GET /odata/Layers({layerId})")]
    public async Task UpdateProtocols_DisableOData_BlocksODataLayerMetadata()
    {
        var body = """
            {
              "enabledProtocols": ["FeatureServer", "MapServer", "OgcFeatures", "Grpc"]
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var updateResponse = await _client.PutAsync("/api/v1/admin/services/test/protocols", content);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var odataResponse = await _client.GetAsync("/odata/Layers(1)");
        odataResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/protocols")]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    public async Task UpdateProtocols_DisableOgcFeatures_BlocksOgcCollectionMetadata()
    {
        var body = """
            {
              "enabledProtocols": ["FeatureServer", "MapServer", "OData", "Grpc"]
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var updateResponse = await _client.PutAsync("/api/v1/admin/services/test/protocols", content);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var ogcResponse = await _client.GetAsync("/ogc/features/collections/1");
        ogcResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
