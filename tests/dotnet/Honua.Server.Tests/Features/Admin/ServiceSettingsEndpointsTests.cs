// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

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
    [Endpoint("GET /rest/services/{serviceName}/FeatureServer/{layerId}")]
    public async Task UpdateLayerMetadata_WithSourceGovernance_UpdatesPublicMetadata()
    {
        var body = """
            {
              "license": "CC-BY-4.0",
              "attribution": "Example contributors",
              "publisher": "Example Data Office",
              "licenseUrl": "https://example.test/licenses/cc-by-4.0",
              "sourceUrl": "https://example.test/data/source"
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/api/v1/admin/services/test/layers/0/metadata", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var adminDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = adminDocument.RootElement.GetProperty("data");
        data.GetProperty("license").GetString().Should().Be("CC-BY-4.0");
        data.GetProperty("attribution").GetString().Should().Be("Example contributors");
        data.GetProperty("publisher").GetString().Should().Be("Example Data Office");
        data.GetProperty("licenseUrl").GetString().Should().Be("https://example.test/licenses/cc-by-4.0");
        data.GetProperty("sourceUrl").GetString().Should().Be("https://example.test/data/source");

        var publicResponse = await _client.GetAsync("/rest/services/test/FeatureServer/0?f=json");
        publicResponse.Be200Ok();
        using var publicDocument = JsonDocument.Parse(await publicResponse.Content.ReadAsStringAsync());
        publicDocument.RootElement.GetProperty("copyrightText").GetString().Should().Be("Example contributors");
        publicDocument.RootElement.GetProperty("license").GetString().Should().Be("CC-BY-4.0");
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    public async Task UpdateLayerMetadata_GovernancePatch_PreservesUnrelatedLinksAndRefreshesLicenseTitle()
    {
        const string oldLicenseUrl = "https://example.test/licenses/cc-by-4.0";
        const string newLicenseUrl = "https://example.test/licenses/mit";
        const string unrelatedLicenseUrl = "https://example.test/legal/alternate-terms";
        const string oldSourceUrl = "https://example.test/data/source";
        const string unrelatedSourceUrl = "https://example.test/schema/collection.json";
        await SetLayerGovernanceMetadataAsync(
            layerId: 0,
            license: "CC-BY-4.0",
            links:
            [
                new MetadataV2Link
                {
                    Href = unrelatedLicenseUrl,
                    Rel = "license"
                },
                new MetadataV2Link
                {
                    Href = oldLicenseUrl,
                    Rel = "license",
                    Title = "Independent mirror"
                },
                new MetadataV2Link
                {
                    Href = oldLicenseUrl,
                    Rel = "license",
                    Type = "text/html",
                    Title = "CC-BY-4.0",
                    Hreflang = "en",
                    ManagedBy = LayerSourceGovernance.LinkManager
                },
                new MetadataV2Link
                {
                    Href = unrelatedSourceUrl,
                    Rel = "describedby",
                    Title = "Collection schema"
                },
                new MetadataV2Link
                {
                    Href = oldSourceUrl,
                    Rel = "describedby",
                    Title = "Source documentation",
                    ManagedBy = LayerSourceGovernance.LinkManager
                }
            ]);

        using (var licenseContent = new StringContent("""{"license":"MIT"}""", Encoding.UTF8, "application/json"))
        {
            var licenseResponse = await _client.PutAsync(
                "/api/v1/admin/services/test/layers/0/metadata",
                licenseContent);
            licenseResponse.Be200Ok();
            using var document = JsonDocument.Parse(await licenseResponse.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            data.GetProperty("license").GetString().Should().Be("MIT");
            data.GetProperty("licenseUrl").GetString().Should().Be(oldLicenseUrl);
        }

        var licenseOnlyMetadata = GetLayerMetadata(0);
        var refreshedLicenseLink = licenseOnlyMetadata.Links.Single(link =>
            link.Href == oldLicenseUrl &&
            link.ManagedBy == LayerSourceGovernance.LinkManager);
        refreshedLicenseLink.Title.Should().Be("MIT");
        refreshedLicenseLink.Type.Should().Be("text/html");
        refreshedLicenseLink.Hreflang.Should().Be("en");
        refreshedLicenseLink.ManagedBy.Should().Be(LayerSourceGovernance.LinkManager);
        licenseOnlyMetadata.Links.Should().ContainSingle(link => link.Href == unrelatedLicenseUrl);
        licenseOnlyMetadata.Links.Should().ContainSingle(link =>
            link.Href == oldLicenseUrl && link.Title == "Independent mirror" && link.ManagedBy == null);

        using (var urlContent = new StringContent(
                   $$"""{"licenseUrl":"{{newLicenseUrl}}","sourceUrl":""}""",
                   Encoding.UTF8,
                   "application/json"))
        {
            var urlResponse = await _client.PutAsync(
                "/api/v1/admin/services/test/layers/0/metadata",
                urlContent);
            urlResponse.Be200Ok();
            using var document = JsonDocument.Parse(await urlResponse.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            data.GetProperty("licenseUrl").GetString().Should().Be(newLicenseUrl);
            AssertAbsentOrNull(data, "sourceUrl");
        }

        var updatedMetadata = GetLayerMetadata(0);
        updatedMetadata.Links.Should().ContainSingle(link =>
            link.Rel == "license" && link.Href == newLicenseUrl && link.Title == "MIT" &&
            link.Type == "text/html" && link.Hreflang == "en" &&
            link.ManagedBy == LayerSourceGovernance.LinkManager);
        updatedMetadata.Links.Should().ContainSingle(link =>
            link.Rel == "license" && link.Href == unrelatedLicenseUrl && link.Title == null &&
            link.ManagedBy == null);
        updatedMetadata.Links.Should().ContainSingle(link =>
            link.Href == oldLicenseUrl && link.Title == "Independent mirror" && link.ManagedBy == null);
        updatedMetadata.Links.Should().ContainSingle(link =>
            link.Rel == "describedby" && link.Href == unrelatedSourceUrl && link.Title == "Collection schema");
        updatedMetadata.Links.Should().NotContain(link => link.Href == oldSourceUrl);

        using (var licenseContent = new StringContent("""{"license":""}""", Encoding.UTF8, "application/json"))
        {
            var licenseResponse = await _client.PutAsync(
                "/api/v1/admin/services/test/layers/0/metadata",
                licenseContent);
            licenseResponse.Be200Ok();
        }

        using (var licenseUrlContent = new StringContent("""{"licenseUrl":""}""", Encoding.UTF8, "application/json"))
        {
            var licenseUrlResponse = await _client.PutAsync(
                "/api/v1/admin/services/test/layers/0/metadata",
                licenseUrlContent);
            licenseUrlResponse.Be200Ok();
        }

        var clearedMetadata = GetLayerMetadata(0);
        clearedMetadata.License.Should().BeNull();
        clearedMetadata.Links.Should().ContainSingle(link =>
            link.Rel == "license" && link.Href == unrelatedLicenseUrl && link.Title == null &&
            link.ManagedBy == null);
        clearedMetadata.Links.Should().NotContain(link =>
            link.Rel == "license" && link.ManagedBy == LayerSourceGovernance.LinkManager);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    public async Task UpdateLayerMetadata_WithMalformedGovernance_ReturnsBadRequest()
    {
        var body = """
            {
              "license": "CC-BY-4.0 OR",
              "sourceUrl": "file:///private/source.txt"
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/api/v1/admin/services/test/layers/0/metadata", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Admin, TestProtocols.FeatureServer, TestProtocols.MapServer, TestProtocols.OgcApiFeatures)]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    [Endpoint("GET /rest/services/{serviceName}/FeatureServer/{layerId}")]
    [Endpoint("GET /rest/services/{serviceName}/MapServer/{layerId}")]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    public async Task LegacyLayer_WithoutSourceGovernance_OmitsOptionalProjectionFields()
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var adminResponse = await _client.PutAsync("/api/v1/admin/services/test/layers/0/metadata", content);
        adminResponse.Be200Ok();
        using (var adminDocument = JsonDocument.Parse(await adminResponse.Content.ReadAsStringAsync()))
        {
            var data = adminDocument.RootElement.GetProperty("data");
            AssertAbsentOrNull(data, "license");
            AssertAbsentOrNull(data, "attribution");
            AssertAbsentOrNull(data, "publisher");
            AssertAbsentOrNull(data, "licenseUrl");
            AssertAbsentOrNull(data, "sourceUrl");
        }

        foreach (var path in new[]
                 {
                     "/rest/services/test/FeatureServer/0?f=json",
                     "/rest/services/test/MapServer/0?f=json"
                 })
        {
            var response = await _client.GetAsync(path);
            response.Be200Ok();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.TryGetProperty("copyrightText", out _).Should().BeFalse();
            document.RootElement.TryGetProperty("license", out _).Should().BeFalse();
            document.RootElement.TryGetProperty("publisher", out _).Should().BeFalse();
            document.RootElement.TryGetProperty("links", out _).Should().BeFalse();
        }

        var ogcResponse = await _client.GetAsync("/ogc/features/collections/0?f=json");
        ogcResponse.Be200Ok();
        using var ogcDocument = JsonDocument.Parse(await ogcResponse.Content.ReadAsStringAsync());
        ogcDocument.RootElement.TryGetProperty("attribution", out _).Should().BeFalse();
        var relations = ogcDocument.RootElement.GetProperty("links").EnumerateArray()
            .Where(link => link.TryGetProperty("rel", out _))
            .Select(link => link.GetProperty("rel").GetString())
            .ToArray();
        relations.Should().NotContain("license").And.NotContain("describedby");
    }

    private static void AssertAbsentOrNull(JsonElement parent, string propertyName)
    {
        if (parent.TryGetProperty(propertyName, out var value))
        {
            value.ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    private async Task SetLayerGovernanceMetadataAsync(
        int layerId,
        string license,
        IReadOnlyList<MetadataV2Link> links)
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var resourceIds = snapshot.Graph.Publications
            .Where(publication => publication.LayerIndex == layerId)
            .Select(publication => publication.ResourceId)
            .ToHashSet(StringComparer.Ordinal);
        var resources = snapshot.Graph.Resources
            .Select(resource => resourceIds.Contains(resource.Metadata.Id)
                ? resource with
                {
                    Metadata = resource.Metadata with
                    {
                        License = license,
                        Links = links
                    }
                }
                : resource)
            .ToArray();

        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
        await store.SaveAsync(snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Resources = resources
        }, expectedEtag: null);
    }

    private MetadataV2ObjectMetadata GetLayerMetadata(int layerId)
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var resourceId = snapshot.Graph.Publications
            .First(publication => publication.LayerIndex == layerId)
            .ResourceId;
        return snapshot.Graph.Resources
            .First(resource => resource.Metadata.Id == resourceId)
            .Metadata;
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
