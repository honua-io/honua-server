// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class FolderPersistenceTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;

    public FolderPersistenceTests()
    {
        _fixture = new WebAppFixture();
        _fixture.ConfigureWebHost(b =>
        {
            b.UseEnvironment("Test");
            b.UseSetting("HONUA_DEV_AUTH", "false");
            b.UseSetting("HONUA_ADMIN_PASSWORD", "folder-persistence-key");
        }).ConfigureServices(services => FolderPostgresTestHost.Configure(services, () => _fixture.CurrentSchema));
    }

    private HttpClient _client = null!;
    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await FolderPostgresTestHost.InstallSchemaAsync(_fixture);
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", "folder-persistence-key"));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/folders")]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/folder")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /rest/services/{folderName}/{serviceId}/FeatureServer")]
    [Endpoint("GET /rest/services/{folderName}/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task FolderMove_AfterRestart_PreservesMembershipAndServicePaths()
    {
        var listing = await _client.GetAsync("/api/v1/admin/folders");
        listing.EnsureSuccessStatusCode();
        _client.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", listing.Headers.ETag!.ToString());
        var created = await _client.PostAsJsonAsync("/api/v1/admin/folders", new { name = "Survey" });
        created.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("data").GetProperty("metadata").GetProperty("id").GetString();
        _client.DefaultRequestHeaders.Remove("If-Match");
        _client.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", created.Headers.ETag!.ToString());
        (await _client.PutAsJsonAsync("/api/v1/admin/services/test/folder", new { folderId = id })).EnsureSuccessStatusCode();
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
        var snapshot = await store.GetCurrentAsync();
        snapshot.Graph.Folders.Should().Contain(folder => folder.Metadata.Id == id);
        snapshot.Graph.Services.Where(service => service.Metadata.Name == "test")
            .Should().OnlyContain(service => service.FolderId == id);
        await RestartHost();
        await AssertServiceReadsAsync();
        using var restartedScope = _fixture.Services.CreateScope();
        var persisted = await restartedScope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>().GetCurrentAsync();
        persisted.Etag.Should().Be(snapshot.Etag);
        persisted.Graph.Folders.Should().Contain(folder => folder.Metadata.Id == id);
        persisted.Graph.Services.Where(service => service.Metadata.Name == "test")
            .Should().OnlyContain(service => service.FolderId == id);
    }

    private async Task RestartHost()
    {
        _client.Dispose();
        await _fixture.RestartHostAsync();
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", "folder-persistence-key"));
    }

    private async Task AssertServiceReadsAsync()
    {
        foreach (var path in new[]
        {
            "/rest/services?f=json",
            "/rest/services/test/FeatureServer?f=json",
            "/rest/services/test/FeatureServer/0/query?where=1%3D1&outFields=*&f=json",
            "/rest/services/Survey/test/FeatureServer?f=json",
            "/rest/services/Survey/test/FeatureServer/0/query?where=1%3D1&outFields=*&f=json"
        })
        {
            var response = await _client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.TryGetProperty("error", out _).Should().BeFalse();
            if (path.Contains("/query?", StringComparison.Ordinal))
                document.RootElement.GetProperty("features").GetArrayLength().Should().BeGreaterThan(0);
        }
    }

}
