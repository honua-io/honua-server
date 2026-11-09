// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Metadata)]
public sealed class AdminLayerDisplaySpatialTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder => builder.UseEnvironment("Test")
            .UseSetting("HONUA_DEV_AUTH", "false")
            .UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword));

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/display")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/display")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task Display_Update_RoundTripsAndChangesProtocolMetadata()
    {
        using var client = _fixture.CreateAdminClient();
        const string path = "/api/v1/admin/metadata/layers/0/display";
        using var response = await PutAsync(client, path,
            """{"minScale":12000,"maxScale":500,"displayField":"name","defaultVisibility":false,"queryable":false,"hasZ":true,"hasM":true}""");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var saved = await ReadAsync(client, path);
        saved.GetProperty("minScale").GetDouble().Should().Be(12000);
        saved.GetProperty("maxScale").GetDouble().Should().Be(500);
        saved.GetProperty("displayField").GetString().Should().Be("name");
        saved.GetProperty("defaultVisibility").GetBoolean().Should().BeFalse();
        saved.GetProperty("queryable").GetBoolean().Should().BeFalse();
        saved.GetProperty("hasZ").GetBoolean().Should().BeTrue();
        saved.GetProperty("hasM").GetBoolean().Should().BeTrue();
        using var protocol = await client.GetAsync("/rest/services/test/FeatureServer/0?f=json");
        protocol.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await protocol.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("minScale").GetDouble().Should().Be(12000);
        document.RootElement.GetProperty("displayField").GetString().Should().Be("name");
        document.RootElement.GetProperty("maxScale").GetDouble().Should().Be(500);
        document.RootElement.GetProperty("defaultVisibility").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("hasZ").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("hasM").GetBoolean().Should().BeTrue();
        using var service = await client.GetAsync("/rest/services/test/FeatureServer?f=json");
        service.StatusCode.Should().Be(HttpStatusCode.OK);
        using var serviceDocument = JsonDocument.Parse(await service.Content.ReadAsStringAsync());
        var advertised = serviceDocument.RootElement.GetProperty("layers").EnumerateArray()
            .Single(layer => layer.GetProperty("id").GetInt32() == 0);
        advertised.GetProperty("minScale").GetDouble().Should().Be(12000);
        advertised.GetProperty("maxScale").GetDouble().Should().Be(500);
        advertised.GetProperty("defaultVisibility").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/spatial")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/spatial")]
    public async Task Spatial_Update_RoundTripsAndPreservesOtherResourceFields()
    {
        using var client = _fixture.CreateAdminClient();
        var before = _fixture.GetCurrentV2GraphSnapshot().Index.ResourcesByStorageLayerId[0];
        const string path = "/api/v1/admin/metadata/layers/0/spatial";
        using var response = await PutAsync(client, path,
            """{"geometryType":"multipoint","supportedCrs":[{"srid":26910,"crs":"EPSG:26910"}],"storageCrs":{"srid":4326,"crs":"EPSG:4326"},"storageCrsCoordinateEpoch":2020.5}""");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var saved = await ReadAsync(client, path);
        saved.GetProperty("supportedCrs")[0].GetProperty("srid").GetInt32().Should().Be(26910);
        saved.GetProperty("storageCrsCoordinateEpoch").GetDouble().Should().Be(2020.5);
        saved.GetProperty("storageCrs").GetProperty("srid").GetInt32().Should().Be(4326);
        saved.GetProperty("geometryType").GetString().Should().Be("multipoint");
        var after = _fixture.GetCurrentV2GraphSnapshot().Index.ResourcesByStorageLayerId[0];
        after.Should().BeEquivalentTo(before with { Spatial = after.Spatial });
        after.Spatial!.SpatialReference.Should().BeEquivalentTo(before.Spatial!.SpatialReference);
        using var clear = await PutAsync(client, path,
            """{"supportedCrs":[],"clearStorageCrs":true,"clearStorageCrsCoordinateEpoch":true}""");
        clear.StatusCode.Should().Be(HttpStatusCode.OK);
        var cleared = await ReadAsync(client, path);
        cleared.GetProperty("supportedCrs").GetArrayLength().Should().Be(0);
        cleared.GetProperty("storageCrs").ValueKind.Should().Be(JsonValueKind.Null);
        cleared.GetProperty("storageCrsCoordinateEpoch").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    public async Task LayerMetadata_Get_ReturnsPreviouslyWrittenGovernance()
    {
        using var client = _fixture.CreateAdminClient();
        const string path = "/api/v1/admin/services/test/layers/0/metadata";
        using var updated = await PutAsync(client, path,
            """{"license":"MIT","publisher":"Survey team","attribution":"Survey credit","licenseUrl":"https://example.com/license","sourceUrl":"https://example.com/data"}""");
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await ReadAsync(client, path);
        saved.GetProperty("license").GetString().Should().Be("MIT");
        saved.GetProperty("publisher").GetString().Should().Be("Survey team");
        saved.GetProperty("sourceUrl").GetString().Should().Be("https://example.com/data");
    }

    [IntegrationTheory]
    [InlineData("display", "{\"minScale\":-1}")]
    [InlineData("display", "{\"minScale\":100,\"maxScale\":500}")]
    [InlineData("spatial", "{\"geometryType\":\"unknown\"}")]
    [InlineData("spatial", "{\"storageCrsCoordinateEpoch\":-1}")]
    [InlineData("display", "{\"displayField\":\"missing-field\"}")]
    [InlineData("spatial", "{\"supportedCrs\":[{\"srid\":-1}]}")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/display")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/spatial")]
    public async Task Update_InvalidValue_RejectsWithoutMutation(string section, string json)
    {
        using var client = _fixture.CreateAdminClient();
        var before = _fixture.GetCurrentV2GraphSnapshot().Etag;
        using var response = await PutAsync(client, $"/api/v1/admin/metadata/layers/0/{section}", json);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _fixture.GetCurrentV2GraphSnapshot().Etag.Should().Be(before);
    }

    [IntegrationTheory]
    [InlineData("/api/v1/admin/metadata/layers/0/display")]
    [InlineData("/api/v1/admin/metadata/layers/0/spatial")]
    [InlineData("/api/v1/admin/services/test/layers/0/metadata")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/display")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/spatial")]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    public async Task Get_AnonymousCaller_RequiresAuthentication(string path)
    {
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTheory]
    [InlineData("/api/v1/admin/metadata/layers/0/display")]
    [InlineData("/api/v1/admin/metadata/layers/0/spatial")]
    [InlineData("/api/v1/admin/services/test/layers/0/metadata")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/display")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/spatial")]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    public async Task Metadata_AuthenticatedReader_IsForbidden(string path)
    {
        using var client = await CreateReaderClientAsync();
        using var read = await client.GetAsync(path);
        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTheory]
    [InlineData("display")]
    [InlineData("spatial")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/display")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/spatial")]
    public async Task Update_AuthenticatedReader_IsForbidden(string section)
    {
        using var client = await CreateReaderClientAsync();
        using var response = await PutAsync(client, $"/api/v1/admin/metadata/layers/0/{section}", "{}");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTheory]
    [InlineData(" NAME ", "name")]
    [InlineData("   ", null)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/display")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/display")]
    public async Task Display_FieldCasingOrWhitespace_PersistsCanonicalNameOrNull(string input, string? expected)
    {
        using var client = _fixture.CreateAdminClient();
        const string path = "/api/v1/admin/metadata/layers/0/display";
        using var response = await PutAsync(client, path, JsonSerializer.Serialize(new { displayField = input }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(client, path)).GetProperty("displayField").GetString().Should().Be(expected);
    }

    [IntegrationTheory]
    [InlineData("GET", "/api/v1/admin/metadata/layers/999999/display")]
    [InlineData("GET", "/api/v1/admin/metadata/layers/999999/spatial")]
    [InlineData("GET", "/api/v1/admin/services/test/layers/999999/metadata")]
    [InlineData("PUT", "/api/v1/admin/metadata/layers/999999/display")]
    [InlineData("PUT", "/api/v1/admin/metadata/layers/999999/spatial")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/display")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/spatial")]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/display")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/spatial")]
    public async Task LayerConfiguration_UnpublishedLayer_ReturnsNotFoundWithoutMutation(string method, string path)
    {
        using var client = _fixture.CreateAdminClient();
        var before = _fixture.GetCurrentV2GraphSnapshot().Etag;
        using var response = method == "GET" ? await client.GetAsync(path) : await PutAsync(client, path, "{}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _fixture.GetCurrentV2GraphSnapshot().Etag.Should().Be(before);
    }

    [IntegrationTheory]
    [InlineData("GET", "/api/v1/admin/metadata/layers/0/display")]
    [InlineData("GET", "/api/v1/admin/metadata/layers/0/spatial")]
    [InlineData("GET", "/api/v1/admin/services/test/layers/0/metadata")]
    [InlineData("PUT", "/api/v1/admin/metadata/layers/0/display")]
    [InlineData("PUT", "/api/v1/admin/metadata/layers/0/spatial")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/display")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/spatial")]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/display")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/spatial")]
    public async Task LayerConfiguration_AmbiguousLayer_ReturnsConflictWithoutMutation(string method, string path)
    {
        SeedAmbiguousLayer();
        using var client = _fixture.CreateAdminClient();
        var before = _fixture.GetCurrentV2GraphSnapshot().Etag;
        using var response = method == "GET" ? await client.GetAsync(path) : await PutAsync(client, path, "{}");
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _fixture.GetCurrentV2GraphSnapshot().Etag.Should().Be(before);
    }

    private void SeedAmbiguousLayer()
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var publication = snapshot.Graph.Publications.First(candidate =>
            candidate.LayerIndex == 1 && candidate.PublicationType == MetadataV2PublicationType.EsriFeatureLayer);
        var service = snapshot.Index.ServicesById[publication.ServiceId];
        var duplicate = service with { Metadata = service.Metadata with { Id = "other-feature-service" } };
        var competing = publication with
        {
            Metadata = publication.Metadata with { Id = "competing-feature-publication" },
            ServiceId = duplicate.Metadata.Id,
            LayerIndex = 0,
            ServiceLocalId = "0",
            IsPrimary = false
        };
        ((TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>()).SetGraph(snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Services = snapshot.Graph.Services.Append(duplicate).ToArray(),
            Publications = snapshot.Graph.Publications.Append(competing).ToArray()
        }, schema: _fixture.CurrentSchema);
    }

    private async Task<HttpClient> CreateReaderClientAsync()
    {
        var key = await _fixture.GetService<IAdminApiKeyStore>().CreateAsync(
            $"metadata-reader-{Guid.NewGuid():N}", ["studio:enduser"], null, null, default);
        return _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", key.Key));
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string path, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PutAsync(path, content);
    }
}
