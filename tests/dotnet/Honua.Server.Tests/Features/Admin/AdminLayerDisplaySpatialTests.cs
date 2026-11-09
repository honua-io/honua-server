// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
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
            """{"supportedCrs":[{"srid":26910,"crs":"EPSG:26910"}],"storageCrs":{"srid":4326,"crs":"EPSG:4326"},"storageCrsCoordinateEpoch":2020.5}""");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var saved = await ReadAsync(client, path);
        saved.GetProperty("supportedCrs")[0].GetProperty("srid").GetInt32().Should().Be(26910);
        saved.GetProperty("storageCrsCoordinateEpoch").GetDouble().Should().Be(2020.5);
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
    [InlineData("/api/v1/admin/services/alpha/layers/0/metadata")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/display")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/spatial")]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/display")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/spatial")]
    public async Task Metadata_AuthenticatedReader_IsForbidden(string path)
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "viewer");
        using var read = await client.GetAsync(path);
        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        if (path.EndsWith("/metadata", StringComparison.Ordinal))
            return;
        using var write = await PutAsync(client, path, "{}");
        write.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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
