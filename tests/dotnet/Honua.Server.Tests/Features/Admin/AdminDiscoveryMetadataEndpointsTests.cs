// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Metadata)]
public sealed class AdminDiscoveryMetadataEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().UseSeed("tests/seed/server.yaml")
        .ConfigureWebHost(builder => builder.UseEnvironment("Test")
            .UseSetting("HONUA_DEV_AUTH", "false")
            .UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword));

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/discovery")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/discovery")]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/popup-info")]
    public async Task UpdateLayer_RoundTripsThroughProtocolAndPreservesOtherMetadata()
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var resource = snapshot.Index.ResourcesByStorageLayerId[0];
        var provider = _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>();
        provider.SetGraph(snapshot.Graph with
        {
            Revision = snapshot.Revision + 1,
            Resources = snapshot.Graph.Resources.Select(candidate => candidate.Metadata.Id == resource.Metadata.Id
                ? candidate with { Metadata = candidate.Metadata with { License = "MIT", Themes = ["transport"] } }
                : candidate).ToArray()
        }, schema: _fixture.MetadataGraphSchema);
        var client = _fixture.CreateAdminClient();
        var path = "/api/v1/admin/metadata/layers/0/discovery";
        var before = await ReadAsync(client, path);
        var response = await UpdateAsync(client, path, before.Etag);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await ReadAsync(client, path);
        saved.Title.Should().Be("Road survey");
        saved.Keywords.Should().Equal("roads", "survey");
        saved.AffectedPublications.Select(impact => impact.PublicationId).Should().BeEquivalentTo(
            snapshot.Graph.Publications.Where(publication => publication.ResourceId == resource.Metadata.Id)
                .Select(publication => publication.Metadata.Id));
        using var popup = new StringContent("""{"title":"{name}"}""", Encoding.UTF8, "application/json");
        (await client.PutAsync("/api/v1/admin/metadata/layers/0/popup-info", popup)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(client, path)).Title.Should().Be(saved.Title);
        var metadata = _fixture.GetCurrentV2GraphSnapshot().Index.ResourcesByStorageLayerId[0].Metadata;
        metadata.License.Should().Be("MIT");
        metadata.Themes.Should().Equal("transport");
        using var protocol = await client.GetAsync("/ogc/features/collections/0?f=json");
        protocol.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await protocol.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("title").GetString().Should().Be(saved.Title);
        document.RootElement.GetProperty("description").GetString().Should().Be(saved.Description);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/discovery")]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/discovery")]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/protocols")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    public async Task UpdateService_UnrelatedProtocolEditPreservesDiscovery()
    {
        var client = _fixture.CreateAdminClient();
        var path = "/api/v1/admin/services/test/discovery";
        var before = await ReadAsync(client, path);
        (await UpdateAsync(client, path, before.Etag)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var capabilities = await client.GetAsync("/ogc/services/test/wms?SERVICE=WMS&REQUEST=GetCapabilities");
        capabilities.StatusCode.Should().Be(HttpStatusCode.OK);
        var xml = XDocument.Parse(await capabilities.Content.ReadAsStringAsync());
        var service = xml.Descendants().Single(element => element.Name.LocalName == "Service");
        service.Elements().Single(element => element.Name.LocalName == "Title").Value.Should().Be("Road survey");
        service.Descendants().Where(element => element.Name.LocalName == "Keyword").Select(element => element.Value)
            .Should().Contain(["roads", "survey"]);
        using var protocols = new StringContent("""{"enabledProtocols":["FeatureServer","OgcFeatures"]}""",
            Encoding.UTF8, "application/json");
        var unrelated = await client.PutAsync("/api/v1/admin/services/test/protocols", protocols);
        unrelated.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await ReadAsync(client, path);
        saved.Title.Should().Be("Road survey");
        saved.Description.Should().Be("Surveyed streets");
        saved.Keywords.Should().Equal("roads", "survey");
        _fixture.GetCurrentV2GraphSnapshot().Graph.Services.Where(service => service.Metadata.Name == "test")
            .Should().OnlyContain(service => service.Metadata.Title == saved.Title);
    }

    [IntegrationTheory]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/discovery")]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/discovery")]
    [InlineData("/api/v1/admin/metadata/layers/0/discovery")]
    [InlineData("/api/v1/admin/services/test/discovery")]
    public async Task UpdateDiscovery_WithStaleEtag_RejectsOverwrite(string path)
    {
        var client = _fixture.CreateAdminClient();
        var before = await ReadAsync(client, path);
        (await UpdateAsync(client, path, before.Etag)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await UpdateAsync(client, path, before.Etag)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/discovery")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/discovery")]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/discovery")]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/discovery")]
    [InlineData("/api/v1/admin/metadata/layers/0/discovery")]
    [InlineData("/api/v1/admin/services/test/discovery")]
    public async Task Discovery_WithoutAdminCredentials_RejectsReadAndWrite(string path)
    {
        var client = _fixture.CreateClient();
        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await UpdateAsync(client, path, "stale")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTheory]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/discovery")]
    [InlineData("{\"etag\":\"stale\",\"title\":\"\\u0000\"}")]
    [InlineData("{\"etag\":\"stale\",\"keywords\":[null]}")]
    [InlineData("{\"etag\":\"\"}")]
    public async Task UpdateDiscovery_InvalidInput_DoesNotMutate(string json)
    {
        var revision = _fixture.GetCurrentV2GraphSnapshot().Revision;
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _fixture.CreateAdminClient().PutAsync("/api/v1/admin/metadata/layers/0/discovery", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _fixture.GetCurrentV2GraphSnapshot().Revision.Should().Be(revision);
    }

    private static async Task<DiscoveryMetadataResponse> ReadAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Deserialize<DiscoveryMetadataResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static Task<HttpResponseMessage> UpdateAsync(HttpClient client, string path, string etag) =>
        client.PutAsJsonAsync(path, new DiscoveryMetadataUpdateRequest
        {
            Etag = etag, Title = "Road survey", Description = "Surveyed streets", Keywords = ["roads", "survey"]
        }, DiscoveryMetadataJsonContext.Default.DiscoveryMetadataUpdateRequest);
}
