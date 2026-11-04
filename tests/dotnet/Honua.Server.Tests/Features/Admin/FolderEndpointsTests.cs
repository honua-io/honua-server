// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class FolderEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().UseSeed("tests/seed/server.yaml")
        .ConfigureWebHost(b =>
        {
            b.UseEnvironment("Test");
            b.UseSetting("HONUA_DEV_AUTH", "false");
            b.UseSetting("HONUA_ADMIN_PASSWORD", "folder-test-key");
        });
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", "folder-test-key"));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/folders")]
    [Endpoint("POST /api/v1/admin/folders")]
    [Endpoint("PATCH /api/v1/admin/folders/{id}")]
    [Endpoint("DELETE /api/v1/admin/folders/{id}")]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/folder")]
    public async Task FolderLifecycle_WithCurrentEtag_MovesAllFacetsAndPreservesAliases()
    {
        var listing = await _client.GetAsync("/api/v1/admin/folders");
        listing.StatusCode.Should().Be(HttpStatusCode.OK);
        var stale = listing.Headers.ETag!.ToString();
        _client.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", stale);
        var created = await _client.PostAsJsonAsync("/api/v1/admin/folders", new { name = "Survey" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var payload = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = payload.RootElement.GetProperty("data").GetProperty("metadata").GetProperty("id").GetString();
        var staleMove = await _client.PutAsJsonAsync("/api/v1/admin/services/test/folder", new { folderId = id });
        staleMove.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        SetEtag(created);
        var moved = await _client.PutAsJsonAsync("/api/v1/admin/services/test/folder", new { folderId = id });
        moved.StatusCode.Should().Be(HttpStatusCode.OK);
        SetEtag(moved);
        var nonempty = await _client.DeleteAsync($"/api/v1/admin/folders/{id}");
        nonempty.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var renamed = await _client.PatchAsJsonAsync($"/api/v1/admin/folders/{id}", new { name = "Survey2", title = "Field survey" });
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);
        SetEtag(renamed);
        var root = await _client.PutAsJsonAsync("/api/v1/admin/services/test/folder", new { folderId = (string?)null });
        root.StatusCode.Should().Be(HttpStatusCode.OK);
        SetEtag(root);
        (await _client.DeleteAsync($"/api/v1/admin/folders/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/folders")]
    public async Task CreateFolder_WithoutPrecondition_Returns428()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/folders", new { name = "Survey" });
        ((int)response.StatusCode).Should().Be(428);
    }

    private void SetEtag(HttpResponseMessage response)
    {
        _client.DefaultRequestHeaders.Remove("If-Match");
        _client.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", response.Headers.ETag!.ToString());
    }
}
