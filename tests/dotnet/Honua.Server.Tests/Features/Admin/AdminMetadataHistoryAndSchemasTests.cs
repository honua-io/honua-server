// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Metadata)]
public sealed class AdminMetadataHistoryAndSchemasTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().UseSeed("tests/seed/server.yaml")
        .ConfigureWebHost(builder => builder.UseEnvironment("Test")
            .UseSetting("HONUA_DEV_AUTH", "false")
            .UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword))
        .ConfigureServices(services =>
        {
            services.RemoveAll<IMetadataV2GraphStore>();
            services.RemoveAll<IMetadataFieldHistoryStore>();
            services.AddSingleton(sp => new RecordingMetadataHistoryStore(
                sp.GetRequiredService<TestMetadataV2GraphProvider>(),
                sp.GetRequiredService<IMetadataActorProvider>()));
            services.AddSingleton<IMetadataV2GraphStore>(sp => sp.GetRequiredService<RecordingMetadataHistoryStore>());
            services.AddSingleton<IMetadataFieldHistoryStore>(sp => sp.GetRequiredService<RecordingMetadataHistoryStore>());
        });

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/layers/{storageLayerId}/history")]
    [Endpoint("GET /api/v1/admin/metadata/history")]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/history")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{storageLayerId}/discovery")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{storageLayerId}/discovery")]
    public async Task EditDiscoveryMetadata_RecordsFieldHistoryVisibleOnItemAndHistoryEndpoint()
    {
        var client = _fixture.CreateAdminClient();
        var before = await ReadDiscoveryAsync(client, "/api/v1/admin/metadata/layers/0/discovery");

        var update = await client.PutAsJsonAsync("/api/v1/admin/metadata/layers/0/discovery", new DiscoveryMetadataUpdateRequest
        {
            Etag = before.Etag,
            Title = "Updated Parcel Title",
            Description = "Tracked change",
            Keywords = ["parcels", "audited"],
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await ReadDiscoveryAsync(client, "/api/v1/admin/metadata/layers/0/discovery");
        after.Title.Should().Be("Updated Parcel Title");
        after.RecentHistory.Should().Contain(entry =>
            entry.FieldPath.Count == 1 && entry.FieldPath[0] == "title"
            && entry.NewValue.HasValue && entry.NewValue.Value.GetString() == "Updated Parcel Title"
            && entry.Actor != "system:metadata-publisher");

        using var historyResponse = await client.GetAsync("/api/v1/admin/metadata/layers/0/history?field=title");
        historyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadHistoryAsync(historyResponse)).Items.Should().Contain(entry =>
            entry.FieldPath.Count == 1 && entry.FieldPath[0] == "title"
            && entry.NewValue.HasValue && entry.NewValue.Value.GetString() == "Updated Parcel Title");

        using var serviceHistoryResponse = await client.GetAsync("/api/v1/admin/services/test/history?limit=5");
        serviceHistoryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadHistoryAsync(serviceHistoryResponse)).Items.Should().NotBeNull();

        using var allHistoryResponse = await client.GetAsync("/api/v1/admin/metadata/history?itemKind=dataset&limit=5");
        allHistoryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadHistoryAsync(allHistoryResponse)).Items.Should().NotBeEmpty();
        _fixture.Services.GetRequiredService<RecordingMetadataHistoryStore>().Queries
            .Should().Contain(query => query.FieldPrefix == "title" && query.ItemKind == "resources" && query.ItemId == before.EntityId);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/schemas")]
    [Endpoint("PUT /api/v1/admin/metadata/schemas")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{storageLayerId}/discovery")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{storageLayerId}/discovery")]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task CustomFieldSchemas_AppearInEditorsSearchFacetsAndCrosswalkExtensions()
    {
        var client = _fixture.CreateAdminClient();
        await DefineStewardSchemaAsync(client);

        var discovery = await ReadDiscoveryAsync(client, "/api/v1/admin/metadata/layers/0/discovery");
        discovery.CustomFieldSchemas.Should().ContainSingle(s => s.Name == "steward" && s.Type == MetadataV2CustomFieldTypes.EnumValue);
        (await PutStewardAsync(client, discovery, "InvalidDept")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PutStewardAsync(client, discovery, "Planning")).StatusCode.Should().Be(HttpStatusCode.OK);

        var recordId = await AssertRecordsSearchFacetsStewardAsync(client);
        using var dcatResp = await client.GetAsync($"/ogc/records/collections/honua-catalog/items/{Uri.EscapeDataString(recordId)}?f=dcat");
        dcatResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var dcatDoc = JsonDocument.Parse(await dcatResp.Content.ReadAsStringAsync());
        dcatDoc.RootElement.GetProperty("honua:customFields").GetProperty("steward").GetString().Should().Be("Planning");

        using var isoResp = await client.GetAsync($"/ogc/records/collections/honua-catalog/items/{Uri.EscapeDataString(recordId)}?f=iso19115-3");
        isoResp.StatusCode.Should().Be(HttpStatusCode.OK);
        XDocument.Parse(await isoResp.Content.ReadAsStringAsync()).Descendants()
            .Single(e => e.Name.LocalName == "supplementalInformation").Value.Should().Contain("\"steward\":\"Planning\"");
    }

    private static async Task DefineStewardSchemaAsync(HttpClient client)
    {
        var initialSchemas = await ReadSchemasAsync(client);
        using var response = await client.PutAsJsonAsync("/api/v1/admin/metadata/schemas", new MetadataCustomFieldSchemasUpdateRequest
        {
            Etag = initialSchemas.Etag,
            Schemas =
            [
                new MetadataV2CustomFieldSchema
                {
                    Name = "steward",
                    Title = "Data Steward",
                    Type = MetadataV2CustomFieldTypes.EnumValue,
                    AllowedValues = ["Planning", "PublicWorks"],
                    AppliesTo = [MetadataV2CustomFieldKinds.Dataset],
                    Facetable = true,
                },
            ],
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static Task<HttpResponseMessage> PutStewardAsync(HttpClient client, DiscoveryMetadataResponse discovery, string steward) =>
        client.PutAsJsonAsync("/api/v1/admin/metadata/layers/0/discovery", new DiscoveryMetadataUpdateRequest
        {
            Etag = discovery.Etag,
            Title = "County Parcels",
            Description = "Parcel boundaries",
            Keywords = ["parcels"],
            CustomFields = new Dictionary<string, JsonElement> { ["steward"] = JsonSerializer.SerializeToElement(steward) },
        });

    private static async Task<string> AssertRecordsSearchFacetsStewardAsync(HttpClient client)
    {
        using var itemsResponse = await client.GetAsync("/ogc/records/collections/honua-catalog/items?custom.steward=Planning");
        itemsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var itemsDoc = JsonDocument.Parse(await itemsResponse.Content.ReadAsStringAsync());
        var features = itemsDoc.RootElement.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
        features[0].GetProperty("properties").GetProperty("honua:customFields").GetProperty("steward").GetString()
            .Should().Be("Planning");
        itemsDoc.RootElement.GetProperty("facets").GetProperty("steward").EnumerateArray()
            .Should().Contain(b => b.GetProperty("value").GetString() == "Planning" && b.GetProperty("count").GetInt32() >= 1);
        return features[0].GetProperty("id").GetString()!;
    }

    [IntegrationTheory]
    [InlineData("standard")]
    [InlineData("agent")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{storageLayerId}/discovery")]
    [Endpoint("PUT /api/v1/admin/metadata/schemas")]
    public async Task DirectMetadataEdit_FromStoredApiKeyOrAgent_RequiresHumanApprovalProposal(string keyKind)
    {
        var admin = _fixture.CreateAdminClient();
        var discovery = await ReadDiscoveryAsync(admin, "/api/v1/admin/metadata/layers/0/discovery");
        var schemas = await ReadSchemasAsync(admin);

        using var keyClient = await CreateStoredKeyClientAsync(keyKind);

        var editResp = await keyClient.PutAsJsonAsync("/api/v1/admin/metadata/layers/0/discovery", new DiscoveryMetadataUpdateRequest
        {
            Etag = discovery.Etag,
            Title = "Blocked Direct Edit",
            Keywords = [],
        });
        editResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editResp.Content.ReadAsStringAsync()).Should().Contain(AgentApprovalFilter.LayerMetadataRef);

        var schemaResp = await keyClient.PutAsJsonAsync("/api/v1/admin/metadata/schemas", new MetadataCustomFieldSchemasUpdateRequest
        {
            Etag = schemas.Etag,
            Schemas = [],
        });
        schemaResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    public async Task SubmitMetadataDiscoveryOperation_FromStandardStoredApiKey_RequiresHumanApproval()
    {
        using var keyClient = await CreateStoredKeyClientAsync("standard");
        using var request = new StringContent(
            """{"parameters":{"storageLayerId":"0","title":"Gateway Direct Edit"}}""",
            Encoding.UTF8,
            "application/json");

        using var response = await keyClient.PostAsync(
            "/api/v1/operations/admin.metadata.layer-discovery.set/submit", request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain(AgentApprovalFilter.LayerMetadataRef);
        (await ReadDiscoveryAsync(_fixture.CreateAdminClient(), "/api/v1/admin/metadata/layers/0/discovery"))
            .Title.Should().NotBe("Gateway Direct Edit");
    }

    private async Task<HttpClient> CreateStoredKeyClientAsync(string keyKind)
    {
        using var keyPayload = new StringContent(
            JsonSerializer.Serialize(new { name = $"metadata-{keyKind}-{Guid.NewGuid():N}", kind = keyKind }),
            Encoding.UTF8,
            "application/json");
        using var createKeyResp = await _fixture.CreateAdminClient().PostAsync("/api/v1/admin/api-keys", keyPayload);
        createKeyResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var keyDoc = JsonDocument.Parse(await createKeyResp.Content.ReadAsStringAsync());
        var apiKey = keyDoc.RootElement.GetProperty("data").GetProperty("key").GetString()!;
        return _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", apiKey));
    }

    private static async Task<DiscoveryMetadataResponse> ReadDiscoveryAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(json, DiscoveryMetadataJsonContext.Default.ApiResponseDiscoveryMetadataResponse)?.Data
            ?? throw new InvalidOperationException("Discovery response data was null.");
    }

    private static async Task<MetadataFieldHistoryResponse> ReadHistoryAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(json, DiscoveryMetadataJsonContext.Default.ApiResponseMetadataFieldHistoryResponse)?.Data
            ?? throw new InvalidOperationException("History response data was null.");
    }

    private static async Task<MetadataCustomFieldSchemasResponse> ReadSchemasAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/admin/metadata/schemas");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(json, DiscoveryMetadataJsonContext.Default.ApiResponseMetadataCustomFieldSchemasResponse)?.Data
            ?? throw new InvalidOperationException("Schemas response data was null.");
    }
}
