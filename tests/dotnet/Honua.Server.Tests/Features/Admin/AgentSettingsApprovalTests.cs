// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class AgentSettingsApprovalTests : IAsyncLifetime
{
    private const string AdminPassword = "agent-settings-human-key";
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .UseSeed("tests/seed/server.yaml")
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
        });
    private HttpClient _human = null!;
    private HttpClient _agent = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _human = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
        var key = await _fixture.Services.GetRequiredService<IAdminApiKeyStore>().CreateAsync(
            "settings-agent", ["admin", "agent"], null, "test", CancellationToken.None);
        _agent = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", key.Key));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{serviceLayerIndex}/metadata")]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task AgentAdministrator_CannotChangeEditingCapabilities(string operation)
    {
        const string path = "/api/v1/admin/services/test/layers/0/metadata";
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var publication = snapshot.Graph.Publications.First(candidate =>
            candidate.PublicationType == MetadataV2PublicationType.EsriFeatureLayer && candidate.LayerIndex == 0);
        var binding = snapshot.ResolveStorageBinding(publication)!;
        _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>().SetGraph(snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            StorageBindings = snapshot.Graph.StorageBindings.Select(candidate => candidate.Metadata.Id == binding.Metadata.Id
                ? candidate with { Capabilities = candidate.Capabilities.Append(MetadataV2StorageBindingCapability.Edit).Distinct().ToArray() }
                : candidate).ToArray()
        }, schema: _fixture.MetadataGraphSchema);
        var enable = "{\"editing\":{\"" + operation + "\":true}}";
        await HumanPutAsync(path, enable);
        await HumanPutAsync(path, """{"editing":{"create":false,"update":false,"delete":false}}""");
        await AssertRejectedWithoutChangesAsync(path, path, enable);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{storageLayerId}/filter")]
    public async Task AgentAdministrator_CannotRemovePermanentFilter()
    {
        var path = $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/filter";
        await HumanPutAsync(path, """{"permanentFilter":{"expression":"category = 'test'","language":"arcgis-sql"}}""");
        await AssertRejectedWithoutChangesAsync(path, path, """{"permanentFilter":null}""");
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{storageLayerId}/fields")]
    public async Task AgentAdministrator_CannotExposeHiddenField()
    {
        var path = $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/fields";
        await HumanPutAsync(path, """{"fields":[{"name":"description","hidden":true}]}""");
        await AssertRejectedWithoutChangesAsync(path, path, """{"fields":[{"name":"description","hidden":false}]}""");
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/protocols")]
    public async Task AgentAdministrator_CannotEnableServiceProtocol()
    {
        const string path = "/api/v1/admin/services/test/protocols";
        await HumanPutAsync(path, """{"enabledProtocols":["FeatureServer"]}""");
        await AssertRejectedWithoutChangesAsync(path, "/api/v1/admin/services/test/settings",
            """{"enabledProtocols":["FeatureServer","OData"]}""");
    }

    private async Task HumanPutAsync(string path, string payload)
    {
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _human.PutAsync(path, content);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task AssertRejectedWithoutChangesAsync(string path, string readPath, string payload)
    {
        var before = await _human.GetAsync(readPath);
        before.StatusCode.Should().Be(HttpStatusCode.OK);
        var original = JsonNode.Parse(await before.Content.ReadAsStringAsync())!.AsObject();
        original.Remove("timestamp");
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _agent.PutAsync(path, content);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var after = await _human.GetAsync(readPath);
        after.StatusCode.Should().Be(HttpStatusCode.OK);
        var current = JsonNode.Parse(await after.Content.ReadAsStringAsync())!.AsObject();
        current.Remove("timestamp");
        JsonNode.DeepEquals(current, original).Should().BeTrue("the response payload must remain unchanged");
        _fixture.GetCurrentV2GraphSnapshot().Graph.Should().BeEquivalentTo(graph);
    }
}
